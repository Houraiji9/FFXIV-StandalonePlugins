# 批量物品交易（BatchTrade）

独立 Dalamud API 15 插件。当前角色向开始时选中的玩家连续赠送一种物品，对方逐笔手动确认；界面可选择背包物品、HQ/NQ、总数量和每笔超时。每笔最多放入 5 个物品堆，最后一格按剩余数量截断。

**当前为初始候选版：源码编译和 38 项纯逻辑检查通过，尚未加载到游戏或执行真实交易。** 运行时只使用宿主提供的 Dalamud、FFXIVClientStructs、Lumina 和 ImGui，不要求安装 DailyRoutines、OmenTools、Artisan 或 AutoRetainer。插件加载后处于空闲状态，不恢复或自动开始上次交易。

## 使用

1. 输入 `/batchtrade` 打开界面，也可从插件列表打开配置。
2. 刷新背包物品，选择物品、品质和总数量。
3. 在游戏中选中接收玩家，确认界面显示的目标后，点击“开始批量交易”。
4. 对方逐笔手动确认。界面显示本笔填充进度、已经核实完成的数量、锁定接收方及当前状态。

随时点击停止按钮，或输入 `/batchtrade stop`。最终确认前会同时取消匹配的当前交易；最终确认已经提交后，只停止后续批次，不代替游戏取消或关闭结算中的窗口，此时需核对本笔实际结果。关闭设置窗口仅隐藏界面；运行中的任务继续，仍可用停止命令终止。

双方身份仅保存在当前任务内存中。配置只保存物品 ID、品质、数量和超时，不保存账号、角色、世界或接收方信息。开始时锁定双方实体 ID、姓名与世界，后续改选目标不会改变接收方。

界面显示的是游戏线程发布的目标和背包快照。开始、停止和刷新操作交给游戏线程执行；开始排队时固定本次物品、品质、数量、超时及界面显示的目标。如果执行时目标已改变，本次请求会停止，需核对后重新点击开始。

## 交易规则

- 只读取普通背包四页；HQ 和 NQ 独立计数。不交易不可交易物品、有精炼度/收藏价值的格或已镶嵌魔晶石的装备。
- 只发送所选物品和规定数量。双方金币必须为零；对方放入物品或金币时停止。插件不自动接受传入交易。
- 逐个核对源格、菜单、数量弹窗和交易栏。每笔填满当前计划并核对全部内容后才确认。
- 数量窗、HQ 警告和最终确认窗必须在预期阶段新建，文本必须与游戏 Addon 表匹配，真实回调接收者必须为 `AgentTrade`，事件种类不得变化。出现不符合条件的弹窗会明确停止。
- 取消、超时、掉线、身份变化、目标离开范围、背包异常或非预期交易内容都会停止，不重发结果不明的交易。其他物品的库存变化也会停止本任务。
- 同一个交易窗口应由一个操作流程控制；使用本插件期间，关闭会操作交易、背包或自动确认弹窗的其他自动化。

只有以下三个信号全部到齐才增加完成数量和开始下一笔：**物品送出日志 `10920`、所选物品库存精确减少本笔数量、原交易已经结束**。允许它们以任意顺序到达，也允许五格物品逐次扣减的中间状态。第一次结算信号到达后有 5 秒核对时间。窗口关闭、单独一条日志或任务变为空闲，都不代表成功。

## 客户端验证边界

构建针对本仓库固定的国服 Soil Dalamud API 15 SDK。原生字段和方法来自该 SDK 的 FFXIVClientStructs，没有新增扫描签名或手写内存偏移。以下仍需在目标客户端首次使用时验证：

1. 正常交易的 `InputNumeric`、HQ 提示和最终 `SelectYesno` 实际回调接收者是否为 `AgentTrade`。如果客户端使用 `InventoryContext` 等其他接收者，插件会停止并给出原因，不会放宽为任意数量框或确认框。
2. 交易预确认按钮 33 是否拥有唯一的本地 `ButtonClick` 事件，接收者是否为当前 Trade 窗口。插件读取其真实事件参数，并使用公开 `ReceiveEvent` 接口；处理器是否接受空事件数据尚未经真实交易验证。
3. 数量框和确认框的实际创建/关闭顺序，以及五格、末格部分数量、HQ 提示、对方取消/修改内容、超时和断线的完整流程。

结算阶段只读核对，最终确认已提交后无法通过本插件撤销。如果交易完成后窗口仍然残留，下一笔会保守停止，需手动核对和关闭窗口。

相关 Addon 文本及类型已用离线游戏数据和 SDK 校验；这不替代真实交互验收。首次验证可用一种低价值物品和小数量，按界面停止原因检查结果。任务停止后应先核对背包，再决定是否手动重新开始。

## 构建与测试

在仓库根目录执行，宿主 SDK 路径及下载方法见根目录说明：

```powershell
dotnet build plugins/BatchTrade/BatchTrade.csproj -c Release
dotnet run --project tests/BatchTrade.Tests/BatchTrade.Tests.csproj -c Release
```

38 项纯逻辑检查包括五格限额、末格余量、HQ 隔离、双向金币/非预期物品拒绝、精确/异常/部分库存变化，以及三个结算信号的全部六种顺序、缺失信号和取消。测试不连接游戏，也不验证原生窗口或网络时序。

## API 依据与许可

- 日志订阅使用宿主公开的 [`IChatGui.LogMessage`](https://github.com/Dalamud-DailyRoutines/Dalamud/blob/4a191c4072677489277f59141b795042c22436ad/Dalamud/Plugin/Services/IChatGui.cs) / `ILogMessage.LogMessageId`。只在回调内读取 ID，不缓存生命周期受限的日志对象，也不调用具有副作用的调试格式化方法。
- 原生结构依据 SDK 固定的 [FFXIVClientStructs 提交 `87d51a7`](https://github.com/Dalamud-DailyRoutines/FFXIVClientStructs/tree/87d51a788ee33329f20b06243be332dd4d35b13c)：`InventoryManager`、`AgentInventoryContext`、`AgentTrade`、`AddonInputNumeric`、`AtkModule.AddonCallbackMapping`、`AtkEvent` 与 `AtkEventListener`。数量框 `[2]` 为最小值、`[3]` 为最大值、`[6]` 为提示文本。
- 原交易发起、取消、按钮节点和确认调用契约参考 [DailyRoutines `AutoSendMoney.cs`（`4c56b56`）](https://github.com/Dalamud-DailyRoutines/DailyRoutines.ModulesPublic/blob/4c56b564fb33b9ac8557e52f76c4ca60f706a8f3/General/AutoSendMoney.cs)。本项目采用 AGPL-3.0 许可并保留许可证；该源码参考不构成运行时依赖。
- 菜单五参数 callback 和数量提交流程参考 [QuickTransfer（`8b41c30`）](https://github.com/Kagekazu/QuickTransfer/tree/8b41c30c08deaedfbb87799bc3c92997a9755a56)，作者 flick、Kagekazu，MIT 许可见 `QuickTransfer.LICENSE` 及仓库第三方声明。未采用其通用自动确认或字符串原地写入逻辑。

宿主依赖不包含在本插件发布包中，其许可证由各上游项目维护。
