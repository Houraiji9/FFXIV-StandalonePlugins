# RetainerVendorSweep 原生直售证据与运行边界

记录日期：2026-11-04。研究与开发只读取源码、SDK 和客户端磁盘文件，没有启动游戏、调用游戏 IPC 或执行实际出售、换号、点击确认。本文的代码可编译和静态证据，不等于真实游戏验收。

## 固定兼容性

- 游戏版本必须精确为 `2026.10.06.0000.0000`。
- 本轮重新读取并计算 `ffxiv_dx11.exe` SHA-256：`958B78E278AD896493C484EB313FF605AB41EDEFEF74F92B0EE078E69B281BF9`。
- 使用仓库固定 API 15 SDK `26-11-03-01`，.NET 10。FFXIVClientStructs 对应 `87d51a788ee33329f20b06243be332dd4d35b13c`。
- 检查公开结构大小：InventoryManager `0x3730`、InventoryItem `0x48`、AgentRetainer `0x68D0`、RetainerManager `0x310`、ShopEventHandler `0x3298`、ShopEventHandler.YesNoProxy `0x30`。
- `CheckSupport()` 在首轮换角前可调用。首次同时检查当前进程主模块名称、磁盘本体摘要与实际映像内确认接收者的虚表；版本／布局每次原生动作前重核。不同版本或摘要拒绝操作，不能只更新 ApplicableVersion 就放行。

RVA 均基于上述客户端；反汇编读取磁盘 PE，并未加载执行其中代码。唯一运行时映像相对地址是确认接收者的精确指针比较：`moduleBase + 0x28C6F30`，虚表 `moduleBase + 0x22CFA90`。该地址来自本轮实际 OpenYesno 调用与静态数据，不是沿用 AR 偏移的猜测；不调用私有函数，不读未公开的对象字段。

公开结构来源：[AgentInventoryContext](https://github.com/aers/FFXIVClientStructs/blob/87d51a788ee33329f20b06243be332dd4d35b13c/FFXIVClientStructs/FFXIV/Client/UI/Agent/AgentInventoryContext.cs)、[AgentRetainer](https://github.com/aers/FFXIVClientStructs/blob/87d51a788ee33329f20b06243be332dd4d35b13c/FFXIVClientStructs/FFXIV/Client/UI/Agent/AgentRetainer.cs)、[ShopEventHandler](https://github.com/aers/FFXIVClientStructs/blob/87d51a788ee33329f20b06243be332dd4d35b13c/FFXIVClientStructs/FFXIV/Client/Game/Event/ShopEventHandler.cs)、[InventoryManager](https://github.com/aers/FFXIVClientStructs/blob/87d51a788ee33329f20b06243be332dd4d35b13c/FFXIVClientStructs/FFXIV/Client/Game/InventoryManager.cs)、[InventoryItem](https://github.com/aers/FFXIVClientStructs/blob/87d51a788ee33329f20b06243be332dd4d35b13c/FFXIVClientStructs/FFXIV/Client/Game/InventoryItem.cs)。

## 范围、入口与数量

服务只扫描当前角色 `Inventory1..4`（容器 0、1、2、3），每页必须已加载且为 35 格。读取原始 `container.Items + slot`，拒绝 symbolic 槽。不会出售雇员仓库、兵装库、装备栏、晶体或市场寄售物品。

每角色只需第一名可用雇员。流程是附近铃 → 自己的新雇员列表 → 选择雇员 → 物品管理 → 角色背包逐堆出售 → 退出自己的雇员界面。铃仅在小于 4.6 米内寻找，不导航。设置目标与交互分开派发，分别等待延迟。

实际动作使用 FFCS 公开继承包装：

```csharp
AgentRetainer.Instance()->HandleCallback((uint)slot, inventoryType, 0, 5);
```

`InventoryContextEvent.HandleCallback` 的公开协议区分命令 5（让雇员出售）和命令 2（市场上架）。本轮 PE 重新检查：

1. `0xCB5F60` 的分派表把命令 5 送到 `0xCB60AE`；命令 2 则走 `0xCB609E → 0xCBB6D0`，两者分离。
2. 直售分支使用公开 `AgentRetainer.ShopEventHandler`，调用 `0x175D880`。该函数先拒绝已有事务，读取给定容器／槽的物品与数量，写 `TransactionItemId`、`SellInventorySlot`、`TransactionItemCount`、`SellInventoryType`，置 `StartingSell=true` 后异步加载物品数据。
3. 请求被本服务同步观察到上述源字段及 `StartingSell=true`，才视为已接收；缺少接收证据就停止，不重复调用。
4. 确认后 `0x17612D0` 在雇员模式调用 `0xCB8990`，再进入 RetainerEvent 路径 `0x17693A0`。后者按冻结容器、槽与完整数量提交直售请求。

此协议出售整堆，没有 InputNumeric 数量窗。本服务不拆分物品、不另行取出、不自动打开 ContextMenu。配置的 ContextMs 实际用于提交整堆直售；QuantityMs 不属于本路径的有效操作。

## 价格与金币账户

本轮 PE `0x175E84E → 0x867FF0` 确认计算顺序：以 Item.PriceLow 为基础；HQ 按 `ceil(price × 11 / 10)`；若带魔晶石，再按 `ceil(previous × (10 + materiaCount) / 10)`。每步独立取整。`0x868BCC…0x868BF8` 从五个魔晶石槽统计非零数量。生产中的纯函数 `VendorNativeRules.TryUnitPrice` 使用相同顺序并检查范围。

在调用直售前就检查本次预计所得和角色金币上限 999,999,999。原生异步报价完成后，再比较公开 `ShopEventHandler.SellPrice` 与冻结单价；不一致不代为确认，未知结果停止。零价、异常数量、收藏品和 Relic 标记物品不进入出售候选。HQ 仅在用户明确启用对应品质时纳入。全局及任何品类的物品保护集合均优先于出售规则。

金币账户的静态依据：`0x175E955` 根据源容器调用 `0x87F550`；后者只把 10000…10006、11000、12000…12002 认作雇员容器。角色背包 0…3 返回 false，`0x175E970` 走 `InventoryManager.GetGil`（`0x8825C0`，读取角色货币容器 2000）。雇员容器才走 `0x87F590`，读取 12000 的雇员金币。这说明客户端按出售源选择资金账户；本服务还以实际角色金币增量作为成交要求，不把客户端预测代替到账。

每次只允许一笔在途出售，并要求：

- 同一角色、雇员、库存窗口生命周期及 ShopEventHandler；原生源字段仍对应本次物品／品质／整堆数量。
- 源格恰好减少该完整数量；其他所有角色背包格的物品、品质、数量、魔晶石、精炼度、耐久、染色与幻化信息保持不变。
- 当前角色金币恰好增加冻结总价，雇员金币不变。

两个结果可以按不同顺序到达，等待时不发第二笔。全部成立只记账一次；自己的事务忙标志或已提交确认窗若稍后才释放，继续只读等待，不重复出售。观察到异常或超时即撤销后续意图，不能以“窗口消失”“队列空了”或短暂无变化当作成交。

## 出售确认窗

部分装备或游戏确认设置会产生 SelectYesno。本轮重新核验 `0x1760350` 的出售确认分支及 `0x1760760`：

- `0x176084B` 把固定 YesNoProxy `image + 0x28C6F30` 作为接收者，eventKind 为 0，进入 `0xED6F0 → 0xEDD70`。
- `0xEE4AE…0xEE4D1` 以 Addon ID 33 建立 SelectYesno，沿用该 receiver／kind；提示文本放在 AtkValues[0]。
- YesNoProxy 的公开字段 `Handler`／`AddonId` 分别对应当前 ShopEventHandler 和这个确认窗；虚表 `0x22CFA90` 的 ReceiveEvent 是 `0x1770F00`。
- `0x1770FF3` 检查第一个 Int：0 才执行；`TransactionType=2` 才走 `0x17612D0` 的出售。其他 Int 值跳过出售。`0x1771028` 清掉当前确认 AddonId。

服务要求该次直售之后出现的新窗口代次、精确 receiver／kind、公开 proxy 的 Handler／AddonId、出售 TransactionType、冻结源与报价、完整背包及金币未变化，再提交一个 Int 0；停止则提交一个 Int 1。既不按“看见是／否窗”泛匹配，也不自动确认市场、取出、交易或其他菜单。

`WaitingForSellConfirm` 可能在原确认回调后仍为 true；`0x175E94A` 在下一次物品报价时才重置。它因此不是下一笔的空闲判断，代码改查实际 proxy.AddonId 和窗口。正常完成相关路径 `0x175CE19`、`0x175CE97`、`0x17615AC` 清事务忙标志；检查范围内未见完成时清空原出售源字段，新的买卖或主动移动源才会重写它们。字段被外部替换仍停止。

## UI 归属、停止与验证边界

铃、列表、动态 SelectString、Talk 和两种库存布局复用仓库现行 MaterialRestockService 的所有权模型，详见 [原生取料证据](retainer-material-native-evidence-v014.md)。本轮游戏本体摘要与该证据一致，复制实现没有修改旧服务。菜单仍按新生命周期、当前 RetainerId、OccupiedSummoningBell、唯一的本地 2378／2383 选项以及当次真实 receiver/kind 联合验证；不能假定动态菜单 receiver 等于 AgentRetainer。

所有原生动作仅由 framework Tick 执行，共享每帧最多一个动作且至少 100 毫秒的门禁，步骤延迟启动时冻结。主动延迟不占响应等待预算。非清理动作还重新调用宿主 CheckConflict，防止 AR 或其他自动流程争用。

Stop 是幂等的，先撤销所有后续出售，不能把等待中的出售重发。若自己的确认窗尚未出现，就继续等原响应；若已出现则取消且只提交一次。取消成功须看到自己的取消提交、该窗口实际 PreFinalize、proxy.AddonId=0、没有尚在开始／执行的事务，并且物品和金币未变化。纯函数 `CancelResponseComplete` 供离线检查验证这组条件。

已发给游戏的出售无法撤回，只能核对原结果。结果不明保持 Failed + Blocking，不继续下一角色。铃、选雇员或打开库存的请求未观察到响应时，也不凭两秒空闲强行释放；仅在已知响应结束且所有自有 UI 释放后完成清理。真正登出／CID 改变使旧会话失效，服务不会对新角色做旧动作。卸载仅撤销后续调度并退订，不在 Dispose 中补做原生操作；仍有窗口或请求时记录需要手动处理。

已完成的是当前文件哈希、关键直售分支、确认接收者、价格及资金账户选择的静态核验，以及固定 SDK 编译。**没有实际游戏出售测试**。发行前的独立代码审查、离线测试结果由根 VALIDATION 另记；真实环境仍需验证铃的对话时序、各种游戏确认设置、异步报价及库存／金币到达顺序、停止时迟到响应。不得把这些未做的实机项目标成通过。
