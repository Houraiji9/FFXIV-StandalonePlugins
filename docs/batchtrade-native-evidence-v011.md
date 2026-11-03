# BatchTrade 0.1.1：交易弹窗静态证据

本记录用于实现审查。检查日期：2026-11-03。只读取客户端 PE 文件、公开源代码和离线表数据；没有执行交易、调用游戏 IPC、修改客户端或验证网络行为。本文不包含客户端二进制、账号、角色信息或个人安装路径。

**0.1.2 勘误：原记录对交易缓存仍保留 symbolic 链接的判断有误。** `InventoryContainer.GetInventorySlot` 会解析链接并返回源背包格；缓存随后复制源物品，再从实际交易格取得交易数量。0.1.1 将缓存必须为 symbolic 作为前提，会拒绝正常物品。完整调用链与正确读取方法见[0.1.2 更正证据](batchtrade-native-evidence-v012.md)。下列相关条目已修正，其他接收者与生命周期证据不因此改变。

## 适用版本

- 客户端 `ffxivgame.ver`：`2026.10.06.0000.0000`。
- 检查的 `ffxiv_dx11.exe` SHA-256：`958b78e278ad896493c484eb313ff605ab41edefef74f92b0ee078e69b281bf9`。
- 检查的 PE 映像基址：`0x140000000`。下列地址均使用 **RVA**，不是可在进程中直接使用的绝对地址。
- 结构声明依据 FFXIVClientStructs commit `87d51a788ee33329f20b06243be332dd4d35b13c`，对应本次 Dalamud API 15 参考程序集。
- 建议 manifest `ApplicableVersion` 和运行期客户端版本检查都限定上述版本。版本字符串相符不是文件字节相同的证明；二进制哈希用于说明本记录实际检查的样本。

运行期可读取 `IDataManager.GameData.Repositories["ffxiv"].Version`。[Lumina Repository.ParseVersion](https://github.com/NotAdam/Lumina/blob/master/src/Lumina/Data/Repository.cs) 对 `ffxiv` 仓库读取游戏根目录的 `ffxivgame.ver`，不是另外的 sqpack 版本，使用 `File.ReadAllText` 且不裁剪空白。应 `Trim()` 后以 ordinal 精确比较；缺失值拒绝。本机文件实际为 20 个 ASCII 字节，没有 BOM、回车或换行。Dalamud 的 `ApplicableVersion` 兼容检查主要用于拒绝较新客户端，因此不能替代运行期精确相等检查。

## 公开结构与原生布局适配

[InventoryManager.cs](https://github.com/Dalamud-DailyRoutines/FFXIVClientStructs/blob/87d51a788ee33329f20b06243be332dd4d35b13c/FFXIVClientStructs/FFXIV/Client/Game/InventoryManager.cs) 声明结构大小 `0x3730`，提供 `Instance()`、贸易双方状态、伙伴、物品槽及 `MoveItemSlot`。该版本没有声明以下两个内嵌回调对象，因此它们是**额外的、依赖客户端版本的原生布局适配**，不能称为全部来自公开字段。

| 窗口 | 实际回调接收者 | EventKind | 文本行 |
| --- | --- | --- | --- |
| 普通玩家交易的物品数量 `InputNumeric` | `InventoryManager.Instance() + 0x3708` | `0` | Addon `206` |
| 玩家交易最终 `SelectYesno` | `InventoryManager.Instance() + 0x36E8` | `0` | Addon `102223` |

两个偏移均在 `0x3730` 大小内。实现只需比较 `AddonCallbackMapping` 返回的接收者地址与这两个预期地址，不需要解引用未建模对象、获取它的私有数据或调用它的虚函数。比较前仍须确认管理器指针非空、运行版本支持、生命周期/阶段符合预期。

数量窗口的旧 `receiver == AgentTrade.Instance()` 条件与以下实际调用链不符；单纯再允许 `AgentInventoryContext` 也不解决本路径。

## 数量窗口调用链

1. `AgentModule` 构造函数 `RVA 0x48D390` 在 `0x48E393` 分配 `0x5A0` 字节，调用 `0xCF3CF0` 构造对象，并在 `0x48E3B5` 存入 `Agents[36]`。这与公开的 `AgentId.Trade = 36` 和 [AgentTrade.cs](https://github.com/Dalamud-DailyRoutines/FFXIVClientStructs/blob/87d51a788ee33329f20b06243be332dd4d35b13c/FFXIVClientStructs/FFXIV/Client/UI/Agent/AgentTrade.cs) 大小、继承关系一致。
2. `AgentTrade` 构造函数给 `+0x28` 的 `InventoryContextEvent` 子对象安装 vtable `RVA 0x21C5F68`，其 `HandleCallback` 为 `RVA 0xCF4990`。该函数在 `0xCF4A37` 选定目标容器 `2005 (0x7D5)`，再调用 `InventoryManager.MoveItemSlot`，`RVA 0x86D470`。
3. 普通背包到 Trade 容器的分支，在 `0x86E1A3` 取 `InventoryManager + 0x3708` 作为数量窗口回调接收者；在 `0x86E1AA` 传入 EventKind `0`；在 `0x86E1C2` 调用数量窗口 helper `RVA 0xCF4AA0`。普通背包分支中的零值来自 `0x86D5A1`，其余会更改该寄存器的特殊背包分支不进入此路径。
4. 数量 helper 构造七个 AtkValue。在 `0xCF4C08` 至 `0xCF4C1B`，仅 EventKind `1` 使用 Addon `425`，否则使用 Addon `206`。物品路径为 EventKind `0`；另一条来自 `AgentTrade.ReceiveEvent` 的 EventKind `1` 是金币数量路径。
5. `0xCF4C75` 调用 `RaptureAtkModule.OpenAddon`，Addon ID `36`（InputNumeric），并传递上述接收者及 EventKind。
6. `InventoryManager` 构造处 `0x896659`/`0x896660` 给 `+0x3708` 安装 vtable `RVA 0x21942D0`。其 `ReceiveEvent` 为 `RVA 0x887110`，在 `0x8871D7` 验证操作目标容器为 `2005`，再处理提交的物品数量。这提供独立的方向验证。

[AddonInputNumeric.cs](https://github.com/Dalamud-DailyRoutines/FFXIVClientStructs/blob/87d51a788ee33329f20b06243be332dd4d35b13c/FFXIVClientStructs/FFXIV/Client/UI/AddonInputNumeric.cs) 定义 `[2]` 为 minimum、`[3]` 为 maximum、`[4]` 为初值、`[6]` 为提示文字。公开源码不保证 maximum 等于原背包堆数。本机普通物品路径先通过 Trade 槽的 `GetLinkedItem` 解析源物品，再将源数量交给数量 helper。实现只需要检查 `minimum <= planned <= maximum`，同时保持计划数不超过冻结源数量、物品堆叠限制和本次配置上限，无须增加 `maximum == sourceQuantity` 这样的额外硬门槛。

## 放入交易槽与数量窗口的先后关系

此处会影响单笔上限为 `1`、部分堆和整堆的全部情况，不能仅修复弹窗 owner。

- `MoveItemSlot` 的普通背包到 Trade 分支，先调用 helper `RVA 0x8710E0`。该 helper 在 `0x8712BD` 调用 `InventoryItem.SetLinkedItem`，将 Trade 槽链接到原背包容器/格；接着在 `0x8712C5`/`0x8712CD` 设置 **Trade 槽本身 Quantity = 1**。该步骤没有从原背包扣除库存。
- 返回后，`0x86E08A` 至 `0x86E096` 检查原物品 `GetQuantity()`。大于 `1` 才继续打开 InputNumeric；小于等于 `1` 跳过数量窗口。因此 `sourceQuantity > 1` 时，即使计划量也是 `1`、当前本方交易数已恰好等于计划，也必须等待并完成本次数量窗口。
- [InventoryItem.cs](https://github.com/Dalamud-DailyRoutines/FFXIVClientStructs/blob/87d51a788ee33329f20b06243be332dd4d35b13c/FFXIVClientStructs/FFXIV/Client/Game/InventoryItem.cs) 公开 `IsSymbolic`（`+0x0E`）、`LinkedItemSlot`（`+0x10`）、`LinkedInventoryType`（`+0x12`）。`ItemId` 与两个链接字段重叠，因此 symbolic Trade 槽不能直接把 `.ItemId` 当物品 ID。
- **已更正：** `RVA 0x87CDD0` 通过容器 `2005` 的 `GetInventorySlot` 取得已解析的源背包格，然后逐字段复制到 `InventoryManager.TradeItemsLocal`，末尾单独从原交易格读取交易数量。逐字段复制不意味着源指针仍指向原始 symbolic 格；旧记录漏查了这个 getter 的实现。
- 对真实原生槽指针调用公开 `GetBaseItemId()`、`GetFlags()`、`GetQuantity()` 可正确读取身份、品质和数量。不要在复制出的托管局部结构上调用这些原生方法。**原始来源链接必须从 `container->Items + i` 检查**，不能用会解引用的 `GetInventorySlot(i)`，也不能要求普通缓存仍保留链接。
- 空缓存槽也已构造：`InventoryManager` 构造函数 `RVA 0x8963B0` 在 `0x896421`/`0x896426` 开始六个本地槽的初始化循环，`0x896443` 为每槽写入 `InventoryItem` vtable `RVA 0x21942E8` 并清空 IsSymbolic、ItemId、Quantity；`0x89648C`/`0x8964A2` 对六个远端槽做相同初始化。因此本版本对两侧前五格（含空格）的真实缓存槽调用 getter，不会仅因为格为空而解引用未初始化 vtable。
- 本机 `GetQuantity` vfunc 12 为 `RVA 0x868940`：非 symbolic 返回本槽 `+0x14`；symbolic 返回 `min(本槽 Quantity, 链接源的 GetQuantity())`。`SetQuantity` vfunc 13 为 `RVA 0x8689B0`，仅写本槽 `+0x14`。因此有效 Trade 数量不是源堆数量的无条件副本。
- 数量 handler `RVA 0x887110` 在 `0x88714A` 取得先前记录的 Trade 目标槽，在 `0x88716B` 对该槽执行 SetQuantity(输入值)，之后才产生数量同步操作。

建议状态顺序：确认原源槽与已有完成槽未变 → 允许当前计划槽出现上述临时 `1` 件链接 → 若源堆大于 `1`，等待本次数量窗口并校验、提交一次 → 等待数量窗口结束和 Trade 槽数量/身份与本次计划完全相符 → 推进下一格。不能用临时 `1` 件误判已经填充完成，也不能把这个原生临时状态一律当作用户篡改。

## 最终确认调用链

1. 贸易状态处理 `RVA 0x87D9A0` 在 `0x87D9D6` 初始化零值寄存器；`0x87DD05` 和 `0x87DD0F` 将双方状态置为 `5`（WaitingForConfirmation）。
2. `0x87DD34` 加载 Addon `102223`；随后加载 `102224`、`102225` 按钮文本。
3. `0x87DDC7` 将接收者指针设为 `InventoryManager + 0x36E8`，`0x87DDCE` 传递 EventKind `0`；`0x87DDE3` 调用 `OpenAddon`，Addon ID `33 (0x21)`（SelectYesno）。
4. 构造处 `0x89663D`/`0x896644` 给 `+0x36E8` 安装 vtable `RVA 0x21942A0`，`ReceiveEvent` 为 `RVA 0x886950`。接受按钮值为 `0`，其交易操作路径写入状态值 `6`。

所以旧的最终确认 `AgentTrade` 接收者门槛同样会拒绝有效窗口。

## PostSetup 可能异步，不能用同步调用作用域作唯一门槛

[AtkUnitBase.cs](https://github.com/Dalamud-DailyRoutines/FFXIVClientStructs/blob/87d51a788ee33329f20b06243be332dd4d35b13c/FFXIVClientStructs/FFXIV/Component/GUI/AtkUnitBase.cs) 定义 `OnSetup` 为 vfunc 49，`IsFullyLoaded` 为 vfunc 66，并区分 LoadingUldResource、LoadingResources、FullyLoaded。

静态代码与上述声明吻合：

- `AtkUnitBase.Open`（由已验证 vtable定位，`RVA 0x66FD80`）把窗口加入管理器待处理列表。
- `RVA 0x67D611` 起处理 LoadState。`0x67D665` 调用 vfunc 66；若资源尚未就绪则退出本次处理，后续循环再继续。
- 达到 FullyLoaded 且尚未设置 IsReady 后，`0x67D6A4` 才调用 setup helper `0x676210`；helper 在 `0x676630` 取 vfunc 49，并在 `0x676641` 或 `0x676681` 调用 OnSetup。
- [Dalamud AddonVirtualTable.cs](https://github.com/Dalamud-DailyRoutines/Dalamud/blob/4a191c4072677489277f59141b795042c22436ad/Dalamud/Game/Addon/Lifecycle/AddonVirtualTable.cs) 在原生 OnSetup 调用后、detour 返回前广播 PostSetup。
- 新建窗口的注册 helper `RVA 0x6A8670` 在 `0x6A86A9` 先调用 addon.Open；`0x6A86B0` 以后才更新 `AddonCallbackMapping`。不能仅凭 PostSetup 事件约定所有外围注册步骤已经返回。

因此 PostSetup 应记录本次待处理菜单动作对应的唯一新生命周期，后续框架帧完成严格绑定和稳定性验证。同步 FireCallback 作用域可以记录为额外诊断，不能要求窗口一定在该作用域内完成 Setup。上述时序分析不是对用户截图具体触发分支的运行观测。

## HQ 文案不能作为玩家交易归属证明

Addon `102434` 的本地文本为“确定要交易优质道具吗？”。该文本至少有三处二进制引用属于 `AgentGrandCompanySupply`（AgentId `96`，构造时写 `Agents[96]`，构造函数 `RVA 0x1002090`、ReceiveEvent `0x1001570`）。这说明通用“交易优质道具”文案也用于 NPC 交付。

本次已核实的玩家交易物品路径不需要据此添加 HQ 自动确认。HQ 物品仍按计划的 Item/HQ 属性校验，通过普通数量和最终确认；额外、未证明归属的 YesNo 窗口应停止，不应自动点选。

## 实现与验证边界

- 保留发送者/收件人身份、当前伙伴、源槽 Item/HQ/数量、贸易内容、操作代次、新窗口生命周期与稳定回调的交叉验证。
- 数量路径只接受上表的数量接收者、EventKind `0` 和 Addon `206`；最终确认只接受对应另一接收者、EventKind `0` 和 Addon `102223`。
- 文本比较应正确读取客户端实际的 String/ConstString 等受支持字符串值；不能因存储类型差异误判相同文案。
- `ContextMenu.PreReceiveEvent` 如用于检测干扰，只处理真正点击/选择事件；正常 MouseOut、Focus 等关闭过程事件不能使已提交菜单动作失效。
- 每个原生动作需经过对应延迟门槛；取消应绑定原 Trade 生命周期代次，防止延迟取消误作用于同一伙伴的新交易。
- 构建、纯逻辑测试和本静态证据均不等于已完成真实玩家交易验证。
