using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using RetainerCraftBridge;
using RetainerVendorSweep;
using System.Text.Json;

namespace RetainerWorkshop;

public sealed class Plugin : IDalamudPlugin, IWorkshopActions
{
    private readonly IDalamudPluginInterface pi;
    private readonly IFramework framework;
    private readonly ICommandManager commands;
    private readonly IDataManager data;
    private readonly IPlayerState player;
    private readonly IPluginLog log;
    private readonly NativeCraftService nativeCraft;
    private readonly MaterialRestockService materials;
    private readonly NativeSaleService nativeSale;
    private readonly WorkshopIpcHost host;
    private readonly CollectionSession collection;
    private readonly CraftPeriod craft;
    private readonly WorkshopRuntime runtime;
    private readonly WorkshopCoordinator coordinator;
    private readonly ScheduleLedger ledger;
    private readonly WorkshopRunJournal journal;
    private readonly OwnedUiSuppression suppression;
    private readonly WorkshopWindow window;
    private readonly object configGate = new();
    private WorkshopConfiguration config;
    private WorkshopUiState view = new(false, "尚未开始。", [], "", [], [], [], false);
    private VendorCharacter[] characters = [];
    private string[] preview = [];
    private ulong previewCid;
    private LegacyImportResult? import;
    private string? configurationIssue;
    private string message = "先刷新角色与配置规则；不会自动开始。";
    private bool requireArtisan;
    private volatile bool disposed;
    private int pending, stopGeneration, stopRequested;
    private long nextUiPublish;

    public Plugin(IDalamudPluginInterface pluginInterface, IFramework framework, ICommandManager commands,
        IClientState client, IPlayerState player, IObjectTable objects, ICondition condition, IGameGui gameGui,
        IDataManager data, IPluginLog log, ITargetManager targets, IAddonLifecycle lifecycle)
    {
        pi = pluginInterface; this.framework = framework; this.commands = commands;
        this.data = data; this.player = player; this.log = log;
        config = LoadConfiguration();
        var directory = pi.GetPluginConfigDirectory();
        ledger = new(Path.Combine(directory, "schedule-ledger.json"));
        journal = new(Path.Combine(directory, "run-journal.json"));
        nativeCraft = new(framework, player, condition, data, lifecycle, log);
        materials = new(framework, player, objects, targets, condition, gameGui, data, lifecycle, log);
        nativeSale = new(framework, player, objects, targets, condition, gameGui, data, lifecycle, log);
        host = new(pi, client, player, objects, condition, gameGui, nativeCraft);
        collection = new(host); craft = new(host, materials);
        runtime = new(host, collection, craft, nativeCraft, materials, nativeSale, SaveSaleProgress);
        coordinator = new(runtime, ledger, journal.Save, () => DateTimeOffset.UtcNow);
        suppression = new(pi, framework);
        window = new(config, data, this);
        pi.GetIpcSubscriber<string, object>("AutoRetainer.OnRetainerAdditionalTask").Subscribe(collection.OnRetainerAdditional);
        pi.GetIpcSubscriber<string, string, object>("AutoRetainer.OnRetainerReadyForPostprocess").Subscribe(collection.OnRetainerReady);
        pi.GetIpcSubscriber<object>("AutoRetainer.OnCharacterAdditionalTask").Subscribe(collection.OnCharacterAdditional);
        pi.GetIpcSubscriber<string, object>("AutoRetainer.OnCharacterReadyForPostprocess").Subscribe(collection.OnCharacterReady);
        commands.AddHandler("/workshop", new CommandInfo(OnCommand) { HelpMessage = "打开雇员工坊；/workshop stop 停止全部自有流程" });
        pi.GetIpcProvider<bool>("RetainerWorkshop.IsBusy").RegisterFunc(() => State.Blocking || Volatile.Read(ref pending) != 0);
        pi.UiBuilder.Draw += Draw; pi.UiBuilder.OpenConfigUi += window.Open; pi.UiBuilder.OpenMainUi += window.Open;
        framework.Update += Update;
    }
    public WorkshopUiState State => Volatile.Read(ref view) with { OperationPending = Volatile.Read(ref pending) != 0 };
    private WorkshopConfiguration LoadConfiguration()
    {
        try
        {
            if(pi.ConfigFile.Exists)
            {
                // Property initializers must not turn a missing protection field into an empty allow-to-sell rule.
                using var document = JsonDocument.Parse(File.ReadAllText(pi.ConfigFile.FullName));
                if(!document.RootElement.TryGetProperty("Sales", out var rules) || rules.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("出售保护结构缺失。");
                foreach(var name in new[] { "ItemIds", "CategoryIds", "ProtectedItemIds", "CategoryProtectedItemIds" })
                    if(!rules.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null)
                        throw new InvalidDataException("出售/保护字段缺失：" + name);
            }
            var loaded = pi.GetPluginConfig();
            if(loaded != null && loaded is not WorkshopConfiguration) throw new InvalidDataException("配置类型不符。");
            var result = loaded as WorkshopConfiguration ?? new();
            if(result.ValidateShape() is { } issue) throw new InvalidDataException(issue);
            return result;
        }
        catch(Exception ex)
        {
            configurationIssue = "保存的配置无法安全读取；请先修复配置文件，未覆盖原文件。";
            log.Error(ex, "RetainerWorkshop configuration rejected");
            return new();
        }
    }
    private void OnCommand(string command, string args)
    {
        if(args.Trim().Equals("stop", StringComparison.OrdinalIgnoreCase)) Stop();
        window.Open();
    }
    private void Draw() { lock(configGate) { if(!disposed) window.Draw(); } }
    private void Queue(System.Action action)
    {
        if(disposed || Interlocked.CompareExchange(ref pending, 1, 0) != 0) return;
        _ = framework.RunOnFrameworkThread(() =>
        {
            try
            {
                lock(configGate)
                    if(!disposed && !framework.IsFrameworkUnloading && framework.IsInFrameworkUpdateThread) action();
            }
            catch(Exception ex) { message = ex.Message; log.Error(ex, "RetainerWorkshop action failed"); }
            finally
            {
                Volatile.Write(ref pending, 0);
                if(!disposed && !framework.IsFrameworkUnloading && framework.IsInFrameworkUpdateThread) Publish();
            }
        });
    }
    public void SaveConfiguration()
    {
        try
        {
            if(configurationIssue != null) throw new InvalidOperationException(configurationIssue);
            if(State.Blocking) return;
            if(config.ValidateShape() is { } issue) throw new InvalidOperationException(issue);
            pi.SavePluginConfig(config); preview = []; previewCid = 0;
        }
        catch(Exception ex) { message = "配置未保存：" + ex.Message; }
    }
    public void Start() => QueueStart(false);
    public void ManualSale() => QueueStart(true);
    private void QueueStart(bool manual)
    {
        WorkshopConfiguration selected;
        try
        {
            if(configurationIssue != null) throw new InvalidOperationException(configurationIssue);
            selected = config.Freeze();
            if(selected.Validate(manual) is { } issue) throw new InvalidOperationException(issue);
        }
        catch(Exception ex) { message = ex.Message; return; }
        var generation = Volatile.Read(ref stopGeneration);
        var expectedCharacters = characters.ToArray();
        Queue(() =>
        {
            if(generation != Volatile.Read(ref stopGeneration)) { message = "开始请求已被停止撤销。"; return; }
            if(coordinator.Blocking || runtime.Blocking) throw new InvalidOperationException("上次流程尚未结束。");
            if(journal.NeedsAttention || ledger.State != LedgerState.Ready)
                throw new InvalidOperationException(journal.NeedsAttention ? journal.Detail : ledger.Issue ?? "请先人工核对中断记录。");
            var fresh = ReadCharacters();
            if(expectedCharacters.Length == 0 || !expectedCharacters.OrderBy(x => x.Cid).SequenceEqual(fresh.OrderBy(x => x.Cid)))
                throw new InvalidOperationException("AR 角色清单已变化或尚未刷新，请重新核对角色选择。");
            ValidateGameData(selected, manual);
            runtime.Prepare(selected, manual);
            if(!suppression.TryAcquire(out var error)) throw new InvalidOperationException(error);
            requireArtisan = !manual && selected.CraftEnabled;
            if(manual) coordinator.StartManualSale();
            else coordinator.Start(selected.Schedule, selected.StopAfterRounds, selected.CraftEnabled);
            preview = []; previewCid = 0;
            message = "本次配置与角色清单已冻结；手动停止优先于所有自动规则。";
        });
    }
    private void ValidateGameData(WorkshopConfiguration selected, bool manual)
    {
        if(!manual && selected.CraftEnabled)
        {
            if(!data.GetExcelSheet<Recipe>().TryGetRow((uint)selected.RecipeId, out var recipe) || recipe.ItemResult.RowId == 0)
                throw new InvalidOperationException("选择的标准配方不存在。");
            if(selected.RecipeModes.GetValueOrDefault(selected.RecipeId, CraftMode.Standard) == CraftMode.Quick && !recipe.CanQuickSynth)
                throw new InvalidOperationException("该配方不支持简易制作。");
        }
        if(manual || selected.Schedule.Enabled)
        {
            var items = data.GetExcelSheet<Item>(); var categories = data.GetExcelSheet<ItemUICategory>();
            if(selected.Sales.ItemIds.Any(id => !items.TryGetRow(id, out var item) || item.PriceLow == 0) ||
                selected.Sales.CategoryIds.Any(id => !categories.TryGetRow(id, out _)))
                throw new InvalidOperationException("出售规则含当前资料中无效的物品或品类。");
        }
    }
    public void Stop()
    {
        Interlocked.Increment(ref stopGeneration); Interlocked.Exchange(ref stopRequested, 1);
        message = "已请求停止后续动作，等待本插件在途操作收尾。";
        // A pending refresh/import/start must never swallow Stop.
        if(!disposed) _ = framework.RunOnFrameworkThread(ApplyStop);
    }
    private void ApplyStop()
    {
        if(disposed || framework.IsFrameworkUnloading || !framework.IsInFrameworkUpdateThread ||
            Interlocked.Exchange(ref stopRequested, 0) == 0) return;
        if(coordinator.Blocking || runtime.Blocking) coordinator.Stop();
    }
    private void Update(IFramework _)
    {
        if(disposed || framework.IsFrameworkUnloading || !framework.IsInFrameworkUpdateThread) return;
        try
        {
            ApplyStop();
            if(previewCid != 0 && (!host.LoggedIn || host.CurrentCid != previewCid)) { preview = []; previewCid = 0; }
            if(coordinator.Blocking || runtime.Blocking)
            {
                if(!suppression.TryAcquire(out var error))
                { coordinator.Fail(error); message = error; Publish(); return; }
                if(coordinator.Stage != WorkshopStage.Stopping)
                {
                    host.ValidateExclusive(requireArtisan, coordinator.Stage == WorkshopStage.Collecting);
                    if(coordinator.Stage == WorkshopStage.Waiting && !runtime.SafeIdle)
                        throw new InvalidOperationException("等待期间出现外部操作，已停止自动交接。");
                }
                coordinator.Tick();
            }
            else suppression.Release();
        }
        catch(Exception ex)
        {
            if(coordinator.Blocking || runtime.Blocking) coordinator.Fail(ex.Message);
            message = "流程已停：" + ex.Message; log.Error(ex, "RetainerWorkshop update failed");
        }
        if(Environment.TickCount64 >= nextUiPublish) { nextUiPublish = Environment.TickCount64 + 200; Publish(); }
    }
    private void Publish()
    {
        lock(configGate) PublishLocked();
    }
    private void PublishLocked()
    {
        string period;
        try
        {
            var value = ledger.GetPeriod(config.Schedule, DateTimeOffset.UtcNow);
            period = (value.PendingPolicy
                ? $"沿用原周期 {value.PeriodKey}：已完成 {value.CompletedRounds} 轮；新设置待生效。"
                : $"{(config.Schedule.Mode == PeriodMode.Daily ? "日" : "周")}周期 {value.PeriodKey}：已完成 {value.CompletedRounds} / {config.Schedule.RoundsBeforeSale} 轮；") +
                $"自动出售：{value.SaleStatus?.ToString() ?? "尚未触发"}。本次运行已完成 {coordinator.CompletedRounds} 轮。";
            if(value.PolicyIssue != null) period += " " + value.PolicyIssue;
        }
        catch(Exception ex) { period = "周期记录：" + ex.Message; }
        var interruption = journal.NeedsAttention || ledger.State == LedgerState.NeedsAttention;
        var results = runtime.SaleResults.Select(x => $"{x.Character.Name}@{x.Character.World}：{x.Status} · 已核实{x.Count}个/{x.Gil}金币").ToArray();
        var detail = coordinator.Detail + "\n" + message;
        if(configurationIssue != null) detail += "\n" + configurationIssue;
        if(journal.NeedsAttention) detail += "\n" + journal.Detail;
        if(ledger.Issue != null) detail += "\n" + ledger.Issue;
        Volatile.Write(ref view, new(coordinator.Blocking || runtime.Blocking, detail, results, period,
            characters.ToArray(), preview.ToArray(), import?.Preview ?? [], import != null && configurationIssue == null,
            interruption, Volatile.Read(ref pending) != 0));
    }
    private VendorCharacter[] ReadCharacters() => host.RegisteredCharacters().Select(x => new VendorCharacter(x.CID, x.Name, x.World)).ToArray();
    public void RefreshCharacters() => Queue(() =>
    {
        if(coordinator.Blocking) return;
        characters = ReadCharacters();
        if(characters.Any(x => x.Cid == 0) || characters.Select(x => x.Cid).Distinct().Count() != characters.Length)
            throw new InvalidOperationException("AR 角色资料不完整，请先刷新 AR 缓存。");
        message = $"读取到 {characters.Length} 个 AR 已知角色；这不是全账号自动发现。";
    });
    public void PreviewInventory()
    {
        SaleRules rules;
        try { rules = config.Sales.Freeze(); } catch(Exception ex) { message = ex.Message; return; }
        Queue(() => ReadInventoryPreview(rules));
    }
    private unsafe void ReadInventoryPreview(SaleRules rules)
    {
        if(coordinator.Blocking || !host.LoggedIn) throw new InvalidOperationException("请在已登录且空闲时预览背包。");
        if(nativeSale.CheckSupport() is { } issue) throw new InvalidOperationException(issue);
        var inventory = InventoryManager.Instance();
        if(inventory == null) throw new InvalidOperationException("库存尚未加载。");
        var lines = new List<string>(); var items = data.GetExcelSheet<Item>(); long quantity = 0;
        for(var page = 0; page < 4; page++)
        {
            var bag = inventory->GetInventoryContainer((InventoryType)page);
            if(bag == null || !bag->IsLoaded || bag->Size != 35) throw new InvalidOperationException("普通背包未完整加载。");
            for(var slotIndex = 0; slotIndex < bag->Size; slotIndex++)
            {
                var slot = bag->GetInventorySlot(slotIndex);
                if(slot == null || slot->Quantity <= 0 || slot->IsSymbolic || !items.TryGetRow(slot->ItemId, out var item)) continue;
                var hq = (slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
                var candidate = new VendorItem(page, slotIndex, slot->ItemId, hq, checked((int)slot->Quantity), item.ItemUICategory.RowId,
                    item.PriceLow, (slot->Flags & InventoryItem.ItemFlags.Collectable) == 0);
                if(!rules.Matches(candidate)) continue;
                quantity = checked(quantity + candidate.Quantity);
                lines.Add($"{item.Name.ExtractText()}{(hq ? " HQ" : " NQ")} × {candidate.Quantity}（背包{page + 1}，格{slotIndex + 1}）");
            }
        }
        previewCid = player.ContentId;
        preview = [$"当前角色匹配 {lines.Count} 格 / {quantity} 个；执行时另行核对原生资格与价格。", .. lines];
        message = "这是当前角色的只读预览，不是其他角色的库存。";
    }
    public void PreviewImport()
    {
        WorkshopConfiguration selected;
        try { selected = config.Freeze(); }
        catch(Exception ex) { message = ex.Message; return; }
        Queue(() =>
    {
        if(coordinator.Blocking) return;
        if(configurationIssue != null) throw new InvalidOperationException(configurationIssue);
        var parent = pi.ConfigFile.DirectoryName ?? throw new InvalidOperationException("无法定位插件配置目录。");
        string? ReadOld(string name)
        {
            var path = Path.Combine(parent, name + ".json");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        if(!LegacyImport.TryPreview(selected, ReadOld("RetainerCraftBridge"), ReadOld("RetainerVendorSweep"), out import, out var error))
            throw new InvalidOperationException(error);
        message = "旧配置仅已读取预览；点击应用后才保存到雇员工坊。";
        });
    }
    public void ApplyImport() => Queue(() =>
    {
        if(coordinator.Blocking || import == null) return;
        var merged = LegacyImport.Apply(config, import);
        pi.SavePluginConfig(merged); config = merged; window.SetConfiguration(config); import = null;
        preview = []; previewCid = 0;
        message = "已导入并保存新配置；原配置保持原状，流程未自动开始。";
    });
    public void AcknowledgeInterruption() => Queue(() =>
    {
        if(coordinator.Blocking || runtime.Blocking || !runtime.SafeIdle)
            throw new InvalidOperationException("请先人工停止 AR/Artisan/Lifestream 并结束所有交互。");
        host.ValidateExclusive(false);
        foreach(var task in ledger.Tasks.Where(x => x.Status is SaleTaskStatus.NeedsAttention or SaleTaskStatus.Failed))
            ledger.AcknowledgeAfterManualCheck(task.TaskId, "用户明确确认已人工核对；保留原周期出售资格已占用，不恢复旧任务。");
        journal.Acknowledge();
        message = "已记录人工核对；未重复执行旧任务，仍需手动开始新运行。";
    });
    private void SaveSaleProgress(string detail)
    {
        var checkpoint = journal.Last ?? throw new InvalidOperationException("出售没有所属运行记录。");
        journal.Save(checkpoint with { Detail = detail, UpdatedUtc = DateTimeOffset.UtcNow });
    }
    public void Dispose()
    {
        if(disposed) return;
        disposed = true; Interlocked.Increment(ref stopGeneration);
        void Cleanup(System.Action action)
        {
            try { action(); }
            catch(Exception ex) { log.Error(ex, "Workshop cleanup failed; check outstanding game actions manually"); }
        }
        try
        {
            Cleanup(() => framework.Update -= Update);
            Cleanup(() => pi.UiBuilder.Draw -= Draw);
            Cleanup(() => pi.UiBuilder.OpenConfigUi -= window.Open);
            Cleanup(() => pi.UiBuilder.OpenMainUi -= window.Open);
            Cleanup(() => commands.RemoveHandler("/workshop"));
            Cleanup(() => pi.GetIpcProvider<bool>("RetainerWorkshop.IsBusy").UnregisterFunc());
            if(coordinator.Blocking || runtime.Blocking)
            {
                Cleanup(coordinator.Stop);
                Cleanup(runtime.DisposeStop);
            }
            Cleanup(() => pi.GetIpcSubscriber<string, object>("AutoRetainer.OnRetainerAdditionalTask").Unsubscribe(collection.OnRetainerAdditional));
            Cleanup(() => pi.GetIpcSubscriber<string, string, object>("AutoRetainer.OnRetainerReadyForPostprocess").Unsubscribe(collection.OnRetainerReady));
            Cleanup(() => pi.GetIpcSubscriber<object>("AutoRetainer.OnCharacterAdditionalTask").Unsubscribe(collection.OnCharacterAdditional));
            Cleanup(() => pi.GetIpcSubscriber<string, object>("AutoRetainer.OnCharacterReadyForPostprocess").Unsubscribe(collection.OnCharacterReady));
            Cleanup(nativeSale.Dispose); Cleanup(nativeCraft.Dispose); Cleanup(materials.Dispose); Cleanup(suppression.Dispose);
        }
        finally { Cleanup(ledger.Dispose); }
    }
}
