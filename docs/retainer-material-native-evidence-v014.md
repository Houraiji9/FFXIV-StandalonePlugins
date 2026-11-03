# 雇员材料预取的静态证据与验证边界（0.1.4）

本文件记录 `MaterialRestockService` 的来源、原生接收者、数量语义和停止边界。研究仅使用公开源码、本机客户端文件的静态反汇编、离线数据和纯托管测试；没有连接游戏执行取出、菜单选择、制作或换角。编译与规则测试不等于游戏内验收。

## 适配范围

- 客户端版本：`2026.10.06.0000.0000`。
- 被检查的 `ffxiv_dx11.exe` SHA-256：`958b78e278ad896493c484eb313ff605ab41edefef74f92b0ee078e69b281bf9`。本文仅保留摘要和 RVA，不分发客户端二进制。
- 下述地址均为 RVA，检查时映像基址为 `0x140000000`；运行代码不使用这些地址寻址或调用私有函数。
- Dalamud API 15，FFXIVClientStructs 对应提交 `87d51a788ee33329f20b06243be332dd4d35b13c`。公开布局大小：`InventoryManager=0x3730`、`AgentRetainer=0x68D0`、`RetainerManager=0x310`。
- `Begin` 与每个原生动作（包括清理）读取 `IDataManager.GameData.Repositories["ffxiv"].Version.Trim()` 并严格匹配版本，同时核对上述布局大小。版本不符不执行原生动作。

公开来源：[AgentRetainer](https://github.com/Dalamud-DailyRoutines/FFXIVClientStructs/blob/87d51a788ee33329f20b06243be332dd4d35b13c/FFXIVClientStructs/FFXIV/Client/UI/Agent/AgentRetainer.cs)、[AgentInventoryContext](https://github.com/Dalamud-DailyRoutines/FFXIVClientStructs/blob/87d51a788ee33329f20b06243be332dd4d35b13c/FFXIVClientStructs/FFXIV/Client/UI/Agent/AgentInventoryContext.cs)、[RetainerManager](https://github.com/Dalamud-DailyRoutines/FFXIVClientStructs/blob/87d51a788ee33329f20b06243be332dd4d35b13c/FFXIVClientStructs/FFXIV/Client/Game/RetainerManager.cs)。

## 功能边界与参考项目

安装的 Artisan 声明版本为 `4.0.5.21+e4380555cac967ec47f976f1b23595a1efbbde73`。公开 `CraftItem(ushort recipeId,int count)` 不包含取料参数；`RetainerInfo.RestockFromRetainers` 是其内部流程，未暴露可由独立插件可靠启动、等待和取消的取料 IPC。AllaganTools 的物品数量 IPC 也是查询接口。

本服务独立实现取料流程，不替换 Artisan、AutoRetainer DLL，不反射其内部成员，也不依赖它们的内部任务队列。流程研究参考 Artisan 的雇员铃、物品管理、数量输入与退出步骤；不照抄其文本点击作为唯一安全依据。

- [Artisan 固定提交](https://github.com/PunishXIV/Artisan/tree/e4380555cac967ec47f976f1b23595a1efbbde73)，AGPL-3.0；参考 `Artisan/IPC/RetainerInfo.cs` 和 `Artisan/Tasks/TaskSelectRetainer.cs`。
- [ECommons RetainerList helper](https://github.com/NightmareXIV/ECommons/blob/8c79525937c02d205f321439036ae2edf63f1956/ECommons/UIHelpers/AddonMasterImplementations/RetainerList.cs) 与 [SelectString helper](https://github.com/NightmareXIV/ECommons/blob/8c79525937c02d205f321439036ae2edf63f1956/ECommons/UIHelpers/AddonMasterImplementations/SelectString.cs) 用于交叉核对公开回调形式。运行时不新增 ECommons 依赖。

## 铃、雇员列表和菜单

服务只查找当前角色附近、可交互且名称匹配 `EObjName#2000401` 的雇员铃。首版采用小于 4.6 米的保守距离，不导航。通过公开 `TargetSystem.InteractWithObject` 发起交互。

`AgentRetainerList` 的构造与回调链：AgentModule 在 `0x492EA0` 附近分配 `0x620` 大小对象并调用 `0xCC0470`；主虚表 `0x21C4820`，`ReceiveEvent=0xCC09E0`。`Show` 的 `0xCC0654` 调用 OpenAddon，传实际 `AgentRetainerList` 指针、事件种类 `0`。选择回调参数 `2,index,empty,empty` 的处理在 `0xCC0D7D` 起读取索引，`0xCC0DF2` 取得公开 `OpenerEventInterface`，`0xCC0E11` 向它交付选中的雇员身份后隐藏列表。

生产代码同时核对：`GetRetainerBySortedIndex` 的当前雇员 ID、可用性、原生列表行名字和当前渲染行名字；不把显示顺序当作永久 ID。名称仅在内存用于匹配，不写入证据或日志。

**SelectString 的接收者不是 AgentRetainer，也不能假定等于列表的 opener。** 原生 `0x17E9770` 以动态场景任务对象作为 `this`；`0x17E9AEC` 的 OpenAddon 传该对象作为 receiver，事件种类为 `0`。其复制构造 `0x17E4C30` 写入虚表 `0x22D7FA0`；`ReceiveEvent=0x179D5F0` 消费菜单索引并转交场景结果。服务不读取这些私有任务字段。

因此菜单归属使用联合约束：自己的铃与角色会话仍有效、`LastSelectedRetainerId` 精确匹配、`OccupiedSummoningBell`、本次动作之后出现的新生命周期、唯一匹配本地 `Addon#2378`（物品管理）或 `Addon#2383`（退出）的菜单行，再冻结 AddonCallbackMapping 中实际 receiver 与 kind=0。提交前重新检查同一指针、代次、接收者及菜单内容。返回列表后 opener 可能是新场景任务，只按该次新列表冻结，不要求跨所有雇员保持一个旧指针。

## 雇员仓库与实际取出

`AgentRetainer` 构造 `0xCB5660` 写主虚表 `0x21C4598`，其公开继承的 `InventoryContextEvent` 子对象在 `+0x28`，虚表 `0x21C4618`，`HandleCallback=0xCB5F60`。FFCS 的继承包装负责子对象调整，生产代码直接调用公开的 `AgentRetainer.HandleCallback`。

仓库显示 `0xCB9610` 的两个布局分支分别使用 Addon ID `0x146` 和 `0x61`；`0xCB9889` 设置 eventKind=0，`0xCB9891` 设置 receiver=AgentRetainer，`0xCB9896` 调用 OpenAddon。因此服务按该 Agent 的实际 `AddonId` 取窗口，不假定用户只使用某一种仓库布局。

公开 `InventoryContextEvent.HandleCallback` 文档定义 callbackParam `3` 为 Retrieve Quantity。静态分支验证如下：

1. `0xCB5FA3` 读取源容器/格的真实数量；`0xCB5FB1` 比较数量是否大于 1。
2. 源数量为 1 时走 `0xCB5FFA → 0xCBCC40`，直接取出这一个物品。不会等待不存在的数量窗。
3. 源数量大于 1 且 callbackParam=3 时，经 `0xCB5FC4 → 0xCB8B30` 请求数量窗口。
4. 源为雇员晶体容器 `12001` 时，目标为角色晶体容器 `2001`、相同晶体格；普通材料走原生背包目的格分配。
5. 取出分支不消费 `InventoryContextFlag` 参数；品质由 `0xCBCC95–0xCBCCC5` 对真实源物品的 getter 读取并传给背包容量/移动路径。调用时传 flags=0 不会把 HQ 转为 NQ。

服务只读取真实雇员容器 `10000…10006,12001` 的已加载原始槽，拒绝 symbolic 槽；不会把缓存数量当成实物，也不读销售、装备等其他容器。每次操作冻结容器、格、物品 ID、HQ、完整源数量及角色完整材料背包总量。提交前重新核对这些字段与同一个仓库窗口。

## 数量窗口

`0xCB8B30` 设置公开 `SellItemInventoryType`、`SellItemInventorySlot`（RVA `0xCB8BD1/0xCB8BD6`），其名称虽含 Sell，但此路径确实被 Retrieve Quantity 复用。

- 7 个 AtkValue；`[2]` 为最小值，`[3]` 为最大值，`[4]` 为初值，`[6]` 为提示。
- 雇员容器分支 `0xCB8E21` 获取 `Addon#914` 的取出数量提示；另一方向使用 `915`，不接受其作为取出提示。
- `0xCB8E67` eventKind=1；`0xCB8E74` receiver=AgentRetainer；`0xCB8E79` OpenAddon ID 36/InputNumeric。
- 主 `ReceiveEvent=0xCB5960` 的 kind=1 分支（`0xCB59C9` 起）消费正整数，通过普通取出或晶体指定目标的移动路径操作。非正值绕过移动并清除冻结源字段，可用于取消该数量请求。

生产代码仅绑定本次 HandleCallback 请求之后的新 InputNumeric 生命周期；在后续框架 tick 等待 mapping 登记，验证 AgentRetainer/kind1、源容器/格、完整源量、提示 914、计划数量落在实际 min/max 范围内后，以公开 FireCallback 提交。数量请求仅发送一次；等待异步 Setup 时取消不会抢先关闭仓库。额外确认窗或上下文菜单均不代确认。

## Talk 对话

Talk 为可复用窗口，不能用“每句都有新 PostSetup”或普通 Agent callback mapping 作为前提。服务只对 Talk 订阅额外 `PostRefresh` 代次；不会用库存窗口的正常 Refresh 使其生命周期租约失效。

本版本 Talk 主虚表为 `0x223D858`，公开 `AtkEventListener.ReceiveEvent`（vfunc2）指向 `0x12C8FE0`。MouseClick=9 分支为 `0x12C919F → 0x12C98F0`，检查可见/Ready/输入阻塞、实际 ResNode target、正常鼠标按钮与非拖拽状态，然后调用 `0x12CA6E0` 补全文字或推进当前句。该函数通过 UI 模块的对话事件 39/55 转交结果，不要求存在普通 AddonCallbackMapping 接收者。

服务仅推进本次选择之后的新 Talk refresh，且当前雇员 ID、铃占用、说话者文本（公开 node2，即 `AddonTalk.AtkTextNode220`）均匹配。事件 target 使用该窗口实际 `WindowCollisionNode`，listener 为 Talk 本身，采用公开 `ReceiveEvent(MouseClick,0,...)` 与正常零初始化鼠标数据；不使用魔法指针 132，不调用私有函数。菜单/列表已经出现时优先处理其已验证语义，不点击背后的对话。

公开结构：[AddonTalk](https://github.com/Dalamud-DailyRoutines/FFXIVClientStructs/blob/87d51a788ee33329f20b06243be332dd4d35b13c/FFXIVClientStructs/FFXIV/Client/UI/AddonTalk.cs)、[AtkEvent](https://github.com/Dalamud-DailyRoutines/FFXIVClientStructs/blob/87d51a788ee33329f20b06243be332dd4d35b13c/FFXIVClientStructs/FFXIV/Component/GUI/AtkEvent.cs)、[AtkEventData](https://github.com/Dalamud-DailyRoutines/FFXIVClientStructs/blob/87d51a788ee33329f20b06243be332dd4d35b13c/FFXIVClientStructs/FFXIV/Component/GUI/AtkEventData.cs)。

## 材料、完成和停止的定义

按配方每次用量乘完整制作次数，包含晶体；当前 NQ/HQ 现存量共同抵扣缺口，但取出时按品质分别核实。普通背包容量按同 ID/同品质的剩余堆叠空间和空格计算，晶体单独核对角色持有上限。材料不足、背包不足、无铃、窗口归属变化或响应超时给出原因，不降制作次数，不在结果不明时重试。

一次取出只有在“冻结源数量减少计划量”与“角色对应 ID/HQ 总量增加计划量”均观察到后才算完成，允许两者不同帧到达；其他物品总量变化、超量、品质变化均阻止自动继续。全部材料到位并且自己的交互已完整释放、稳定 2 秒后，服务才报告 Completed。

每个原生动作至少间隔 100 ms、每框架帧最多一个，延迟在 Begin 冻结。Bell/选择雇员/打开仓库/取出/数量提交/清理均有可调延迟；配置等待不消耗响应超时。仓库 Hide、退出菜单、关闭列表均单次发送，不因窗口尚未消失反复发送。

**停止立即撤销后续取料意图，但不能撤回已发送给游戏的请求。** `MaterialResponse` 分别记录铃列表、雇员菜单、仓库、数量窗、关闭仓库后菜单及退出后列表。未观察到正确响应时，空窗口、2 秒静默和超时都不是取消成功证据；保持 Blocking 并明确提示，不让主流程恢复 AutoRetainer。已绑定的响应只能关闭自己的窗口。已发取出结果不明时同样不自动释放或重试；角色会话失效后旧请求作废，服务不操作新角色。卸载只撤销后续动作并退订，不绕过延迟发原生清理。

`tests/MaterialRestock.Tests` 检查材料计算、容量、异步两侧更新及每类请求的未见/已见/关闭/稳定/角色失效决策。这些测试不覆盖游戏 UI 的实际加载时序、网络响应、其他插件抢占、所有雇员对话样式或真实取消效果；仍需用户在游戏中验收。

## YesAlready 的协作暂停

本机 YesAlready 声明版本 `1.14.14.0+8979c39ff3d1d3c362f82965bceb71b9fd4e5d5a`，DLL SHA-256 `B891F5B5D9AE6A8BECC6E9460BBF33D93DC810FC1B7794B7AA82B50AF5D44494`。其静态 PE 元数据与 IL 确认：`YesAlready.IPC.BlockListHandler` 构造取得共享 `HashSet<string>`，用户字符串 token `0x70002FF4` 为 `YesAlready.StopRequests`；`get_Locked` 读取集合 Count 并返回 `Count > 0`；`YesAlready.get_Active` 要求配置启用且未 Locked。这里没有加载或执行插件程序集。

公开上游同一契约可见 [BlockListHandler.cs](https://github.com/PunishXIV/YesAlready/blob/659c0a1640c93199c92ce2473348e776ef79064d/YesAlready/IPC/BlockListHandler.cs) 与 [YesAlready.cs](https://github.com/PunishXIV/YesAlready/blob/659c0a1640c93199c92ce2473348e776ef79064d/YesAlready/YesAlready.cs)。该上游提交用于交叉核对公开接口，不冒充已安装 DLL 的声明提交。安装版本对应提交未在官方仓库取到，因此保留本机 IL 验证与上游源码的区别。

`OwnedUiSuppression` 仅通过 Dalamud 的公开 `TryGetData<HashSet<string>>` 访问已存在的集合，使用本次插件实例唯一 owner。开始前及整个 Blocking 期间每次执行自有队列前重新确认暂停请求，处理集合被清空或替换的情况；结束/卸载只移除自己的 owner。不会 Clear 集合、修改 YesAlready 配置、反射内部字段或主动创建不存在的服务。YesAlready 未加载时不新增依赖；已加载但接口缺失或操作异常时返回失败，主流程停止发送新的界面动作。
