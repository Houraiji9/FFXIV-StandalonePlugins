using RetainerCraftBridge;
using RetainerVendorSweep;

namespace RetainerWorkshop;

internal sealed class WorkshopSaleHost(WorkshopIpcHost host) : ISweepHost
{
    private ulong requested;
    public ulong CurrentCid => host.CurrentCid;
    public bool LoggedIn => host.LoggedIn;
    public bool SafeForSwitch => host.UiReleased && !host.ArMultiEnabled && host.ArtisanIdle;
    public bool IsSwitching
    {
        get
        {
            var busy = host.NavigationBusy;
            if(!busy && LoggedIn && CurrentCid == requested) requested = 0;
            return busy;
        }
    }
    public string? Conflict()
    {
        try
        {
            host.ValidateExclusive(false);
            if(!host.ArtisanIdle) return "Artisan 被其他制作流程占用。";
            if(requested == 0 && host.ArBusy) return "AR 或外部导航仍在运行。";
            return null;
        }
        catch(Exception ex) { return ex.Message; }
    }
    public void PrepareCharacter()
    {
        if(!SafeForSwitch || IsSwitching) throw new InvalidOperationException("出售角色尚未空闲。");
        if(Conflict() is { } issue) throw new InvalidOperationException(issue);
        host.DisableArFunctions();
    }
    public void ChangeCharacter(VendorCharacter character)
    {
        if(!SafeForSwitch || IsSwitching) throw new CharacterSwitchRejectedException("未到可安全换角状态。");
        if(Conflict() is { } issue) throw new CharacterSwitchRejectedException(issue);
        requested = character.Cid;
        try { host.Relog(character.Name, character.World); }
        catch { requested = 0; throw; }
    }
}

internal sealed class WorkshopRuntime : IWorkshopRuntime
{
    private readonly WorkshopIpcHost host;
    private readonly CollectionSession collection;
    private readonly CraftPeriod craft;
    private readonly NativeCraftService nativeCraft;
    private readonly MaterialRestockService materials;
    private readonly NativeSaleService nativeSale;
    private readonly SaleBatch sales;
    private WorkshopConfiguration config = new();
    private CyclePlan? collectionPlan;
    private CharacterIdentity? crafter;
    private VendorCharacter[] saleCharacters = [];
    private bool transfer, switchSent, transferStopping, transferFailed, craftSkipped, transferAbortRequested;
    private long switchDue, switchDeadline;
    private long? stableAt;
    private string transferDetail = "";
    private enum Active { None, Collection, Sale, Craft }
    private Active active;

    public WorkshopRuntime(WorkshopIpcHost host, CollectionSession collection, CraftPeriod craft,
        NativeCraftService nativeCraft, MaterialRestockService materials, NativeSaleService nativeSale,
        Action<string> saveSaleProgress)
    {
        this.host = host; this.collection = collection; this.craft = craft;
        this.nativeCraft = nativeCraft; this.materials = materials; this.nativeSale = nativeSale;
        var saleHost = new WorkshopSaleHost(host);
        nativeSale.CheckConflict = saleHost.Conflict;
        sales = new(saleHost, nativeSale, saveSaleProgress);
    }
    public bool Blocking => transfer || collection.Blocking || craft.Blocking || nativeCraft.Blocking
        || materials.Blocking || sales.Blocking || nativeSale.Blocking;
    public bool SafeIdle => host.UiReleased && !host.ArMultiEnabled && !host.ArBusy && !host.NavigationBusy && host.ArtisanIdle;
    public bool FirstReady => collectionPlan != null && host.FirstReady(collectionPlan);
    public SessionStatus CollectionState => collection.State;
    public SessionStatus CraftState => transferFailed ? SessionStatus.Failed : transfer ? SessionStatus.Running : craftSkipped ? SessionStatus.Stopped : craft.State;
    public SaleBatchState SaleState => sales.State;
    public IReadOnlyList<SweepResult> SaleResults => sales.Results;
    public string Detail => active switch
    {
        Active.Collection => collection.Detail,
        Active.Sale => sales.Detail,
        Active.Craft => transfer || transferFailed ? transferDetail : craft.Detail,
        _ => "等待手动开始。"
    };
    public void Prepare(WorkshopConfiguration selected, bool manualSale)
    {
        if(Blocking) throw new InvalidOperationException("上次操作尚未释放。");
        if(selected.Validate(manualSale) is { } invalid) throw new InvalidOperationException(invalid);
        var snapshot = selected.Freeze();
        host.ValidateExclusive(snapshot.CraftEnabled && !manualSale);
        if(!SafeIdle) throw new InvalidOperationException("请先关闭 AR 多角色模式，并等待其他任务及窗口退出。");
        var known = host.RegisteredCharacters();
        if(known.Any(x => x.CID == 0 || string.IsNullOrWhiteSpace(x.Name) || string.IsNullOrWhiteSpace(x.World)) ||
            known.Select(x => x.CID).Distinct().Count() != known.Count)
            throw new InvalidOperationException("AR 角色缓存不完整或有重复身份。");
        collectionPlan = manualSale ? null : host.PrepareCollection(snapshot.FirstCharacterCid, snapshot.LastCharacterCid, snapshot.CraftEnabled);
        crafter = null;
        if(!manualSale && snapshot.CraftEnabled)
        {
            var character = known.SingleOrDefault(x => x.CID == snapshot.CraftCharacterCid)
                ?? throw new InvalidOperationException("制作角色不在 AR 已知角色清单中。");
            crafter = new(character.CID, character.Name, character.World);
        }
        saleCharacters = known.Where(x => !snapshot.ExcludedSaleCharacters.Contains(x.CID))
            .OrderBy(x => snapshot.SaleCharacterOrder.IndexOf(x.CID) is var i && i >= 0 ? i : int.MaxValue)
            .Select(x => new VendorCharacter(x.CID, x.Name, x.World)).ToArray();
        if(manualSale || snapshot.Schedule.Enabled)
        {
            if(saleCharacters.Length == 0) throw new InvalidOperationException("请至少选择一个出售角色。");
            if(nativeSale.CheckSupport() is { } unsupported) throw new InvalidOperationException(unsupported);
        }
        config = snapshot;
        host.ConfigureDelays(config.RotationDelays); craft.ConfigureDelays(config.RotationDelays);
        host.RequestedCraftMode = config.RecipeModes.GetValueOrDefault(config.RecipeId, CraftMode.Standard);
        nativeCraft.Delays = config.CraftDelays; materials.Delays = config.MaterialDelays;
        transfer = switchSent = transferStopping = transferFailed = craftSkipped = transferAbortRequested = false; stableAt = null; active = Active.None;
    }
    public void BeginCollection()
    {
        if(Blocking || collectionPlan == null) throw new InvalidOperationException("尚不能开始下一轮收取。");
        host.ValidateExclusive(config.CraftEnabled);
        active = Active.Collection;
        collection.Begin(collectionPlan, config.RotationDelays);
    }
    public void TickCollection()
    {
        host.ValidateExclusive(config.CraftEnabled, allowMultiMode: true);
        if(!host.ArtisanIdle) throw new InvalidOperationException("收取期间 Artisan 被其他任务启动。");
        collection.Tick();
    }
    public void BeginSale(string taskId)
    {
        if(Blocking) throw new InvalidOperationException("其他阶段尚未收尾，不能开始出售。");
        host.ValidateExclusive(false); active = Active.Sale;
        sales.Begin(taskId, saleCharacters, config.Sales, config.SaleDelays, host.Now);
    }
    public void ResumeSale()
    {
        if(Blocking) throw new InvalidOperationException("收取尚未收尾，不能恢复出售。");
        host.ValidateExclusive(false); active = Active.Sale; sales.Resume(host.Now);
    }
    public void TickSale(bool pauseAfterCharacter)
    {
        host.ValidateExclusive(false); sales.Tick(host.Now, pauseAfterCharacter);
    }
    public void BeginCraft()
    {
        if(Blocking || crafter == null || !config.CraftEnabled) throw new InvalidOperationException("制作尚未准备好。");
        host.ValidateExclusive(true); active = Active.Craft;
        transfer = true; switchSent = transferStopping = transferFailed = craftSkipped = transferAbortRequested = false; stableAt = null;
        switchDue = host.Now + config.RotationDelays.RelogMs;
        switchDeadline = switchDue + config.RotationDelays.RelogTimeoutSeconds * 1000L + config.RotationDelays.LoginStableMs;
        transferDetail = "准备返回配置的制作角色并核对身份。";
    }
    public void TickCraft()
    {
        host.ValidateExclusive(true);
        if(transfer) { ObserveTransfer(); return; }
        if(host.ArtisanForeignControl || host.ArBusy || host.NavigationBusy)
        {
            craft.Fail("制作控制权发生变化；已撤销未发步骤，等待冲突解除后收尾。");
            nativeCraft.Stop();
            throw new InvalidOperationException("制作前检查发现外部 Artisan/AR/导航任务。");
        }
        nativeCraft.Tick(); craft.Tick();
    }
    private void ObserveTransfer()
    {
        if(crafter == null) throw new InvalidOperationException("制作角色丢失。");
        if(host.LoggedIn && host.CurrentCid == crafter.Cid && SafeIdle)
        {
            stableAt ??= host.Now;
            if(host.Now - stableAt < config.RotationDelays.LoginStableMs) return;
            transfer = switchSent = false;
            if(!transferStopping && !transferFailed)
                craft.Begin(crafter, checked((ushort)config.RecipeId), config.CraftCount, config.RestockMaterials);
            else craftSkipped = true;
            return;
        }
        stableAt = null;
        if(host.Now > switchDeadline)
        {
            transferFailed = true; transferStopping = true;
            transferDetail = "返回制作角色超时；已发换角仍需核实，不重发且不开始制作。";
            if(!switchSent) transfer = false;
            return;
        }
        if(transferStopping || host.Now < switchDue || switchSent || !SafeIdle) return;
        switchSent = true; // A throwing IPC may have submitted the change already.
        try { host.Relog(crafter.Name, crafter.World); }
        catch(CharacterSwitchRejectedException) { transfer = switchSent = false; transferFailed = true; throw; }
    }
    public void StopCraft()
    {
        transferStopping = true;
        if(transfer && !switchSent) { transfer = false; craftSkipped = true; }
        craft.Stop(); nativeCraft.Stop();
    }
    public void StopAll()
    {
        // Cancel only stages created by this runtime; a settings/preflight error must not abort foreign jobs.
        collection.Stop(); craft.Stop(); sales.Stop(host.Now);
        if(nativeCraft.Blocking) nativeCraft.Stop();
        if(materials.Blocking) materials.Cancel();
        if(transfer)
        {
            transferStopping = true;
            if(switchSent && !transferAbortRequested)
            {
                // IPC may succeed before throwing; never abort a later foreign task on a repeated Stop.
                transferAbortRequested = true;
                host.AbortNavigation();
            }
            else if(!switchSent) transfer = false;
        }
    }
    public void Drain()
    {
        if(collection.Blocking) collection.Tick();
        if(transfer) ObserveTransfer();
        if(nativeCraft.Blocking || craft.Blocking)
        {
            // Cancellation intent is already applied. Never send native cleanup into foreign control.
            if(host.ArtisanForeignControl || host.ArBusy || host.NavigationBusy) return;
            if(nativeCraft.Blocking) nativeCraft.Tick();
            if(craft.Blocking) craft.Tick();
        }
        if(sales.Blocking) sales.Tick(host.Now, false);
    }
    public void DisposeStop()
    {
        StopAll(); collection.DisposeStop(); craft.DisposeStop();
    }
}
