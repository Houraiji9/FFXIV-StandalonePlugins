using System.Text.Json;

namespace RetainerWorkshop;

internal sealed class WorkshopRunJournal
{
    private readonly string path;
    public bool NeedsAttention { get; private set; }
    public string Detail { get; private set; } = "";
    public WorkshopCheckpoint? Last { get; private set; }
    public WorkshopRunJournal(string path)
    {
        this.path = path;
        try
        {
            if(!File.Exists(path)) return;
            Last = JsonSerializer.Deserialize<WorkshopCheckpoint>(File.ReadAllText(path))
                ?? throw new InvalidDataException("运行记录为空。");
            Validate(Last);
            var stage = Enum.Parse<WorkshopStage>(Last.Stage);
            NeedsAttention = stage is not (WorkshopStage.Idle or WorkshopStage.Stopped or WorkshopStage.LimitReached or WorkshopStage.Failed);
            if(NeedsAttention) Detail = "上次运行未记录安全结束，加载不会恢复动作；请先人工确认游戏、AR、Artisan及窗口已停止。";
        }
        catch(Exception ex) { NeedsAttention = true; Detail = "无法读取上次运行记录，禁止自动开始：" + ex.Message; }
    }
    public void Save(WorkshopCheckpoint checkpoint)
    {
        Validate(checkpoint);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".new";
        using(var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, checkpoint, new JsonSerializerOptions { WriteIndented = true });
            stream.Flush(true);
        }
        File.Move(temporary, path, true);
        Last = checkpoint;
        NeedsAttention = false; Detail = "";
    }
    public void Acknowledge()
    {
        Save(new(Guid.NewGuid(), WorkshopStage.Stopped.ToString(), 0, true, null, [],
            "用户已人工检查上次中断；不恢复旧动作。", DateTimeOffset.UtcNow));
    }
    private static void Validate(WorkshopCheckpoint value)
    {
        if(!Enum.TryParse<WorkshopStage>(value.Stage, out var stage) || !Enum.IsDefined(stage) ||
            value.RunId == Guid.Empty || value.CompletedRounds < 0 || value.PendingSaleTasks == null ||
            value.PendingSaleTasks.Any(string.IsNullOrWhiteSpace) || value.Detail == null ||
            value.UpdatedUtc == default || value.SaleTaskId != null && string.IsNullOrWhiteSpace(value.SaleTaskId))
            throw new InvalidDataException("运行记录字段不完整或无效。");
    }
}
