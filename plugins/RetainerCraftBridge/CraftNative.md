# 明确制作模式的本地调度

`NativeCraftService` 负责用户明确选择的标准制作和简易制作。继承 Artisan 的模式仍走原有 IPC；两条路径不会同时启动。明确模式不调用 `Artisan.CraftItem`，不改 Artisan 配置、不使用其私有队列、不反射插件对象。

标准制作要求用户事先开启 Artisan 自动模式。插件只启动标准工艺，实际技能选择继续由 Artisan 当前配方的求解器或宏执行。它不代替 Artisan 的食物、药品、换职业、装备和修理等外层准备流程。当前角色必须已是配方对应职业，制作按钮必须可用。暂只处理普通配方；特殊手册和额外确认窗口会停止任务。

制作次数表示**尝试次数**。标准失败记为一次尝试并停止，不额外补做。简易制作单批最多 99 次，后续批次重新读取材料和剩余次数。产出数量不会反向增加请求次数。一次批次的实际开始、结束、配方和产出必须能对应；结束后另有 5 秒只读结算窗口，仍不确定时停止并报告，不盲目重发。

材料选择遵循 NQ 优先，HQ 只补实际缺口。先核对配方数据表与手册的六个原料格、背包和水晶总量，再调用游戏自身的全 NQ 填充。随后逐份追加不足的 HQ；每次等实际已选数量变化，再继续下一步。开始前重新核对每格的物品、品质数量和实际库存。不会直接改私有内存字段来伪造材料充足。

每个发出动作均有冻结到本次任务的独立延迟：打开配方、分配材料、启动制作、提交简易数量、取消简易批次、关闭自有窗口、两次制作间隔。可调范围为 100–30,000 毫秒；同一框架帧最多一个动作，所有动作至少间隔 100 毫秒。等待超时不包含这些人为延迟，正在实际执行的标准制作也不会被普通响应超时中断。

停止立即取消未发送的步骤。标准制作等待当前工艺结束，再关闭自己拥有的手册。简易制作向已核验的本批进度窗口请求取消；游戏的取消接口**不保证当前一件先完成**。已经发出的打开手册、打开数量框或开始制作请求，若回应迟到，仍保留所有权等待清理，不把短暂没有窗口当成成功取消。卸载不绕过延迟补发操作；有未结束工艺时提示手动处理。

## 静态核验的原生协议

仅适配客户端 `2026.10.06.0000.0000`。版本来自 Lumina 的 `ffxiv` repository，去除末尾空白后精确比较。静态样本 SHA-256 为 `958B78E278AD896493C484EB313FF605AB41EDEFEF74F92B0EE078E69B281BF9`。下列地址均为该样本 RVA，公开字段来自固定 FFCS 提交 `87d51a788ee33329f20b06243be332dd4d35b13c`。

| 操作 | 严格归属和协议 | 静态证据 |
|---|---|---|
| 制作手册 | `AgentRecipeNote`，事件 0；公开 `OpenRecipeByRecipeId` | `C8C36A` 创建 RecipeNote（76），接收者为同 agent、事件 0 |
| 全 NQ 分配 | `RecipeNote.FireCallback(2, [6, 0xFFFF], true)` | callback 6 在 `C8B1B8` 读取第二参数；`BEC6C0` 全量填充，NQ 不足时不会主动补 HQ |
| 补一个 HQ | 同上，第二参数为 `ingredientIndex \| 0x10000` | `BEC650` 只追加一个 HQ；先核已选总量未满及真实 HQ 库存 |
| 标准开始 | 同属手册，callback 8 | `C8B241` 调用 `BECBB0`；成功后 `BECC3F` 写 `RecipeNote.CraftingRecipeId` |
| 简易数量框 | `AgentRecipeNote`，事件 1，自己的新生命周期 | `C8B520` 创建 SynthesisSimpleDialog（235）；四个 AtkValue，前两项提供可制作数量 |
| 简易数量提交 | `[Int 数量, Bool true, Bool true]`，1–99 且不超过库存/对话框上限/剩余次数 | 事件 1 分支 `C89EC0` 调用 `BECD10`；与 Artisan 公开数量提交路径一致 |
| 简易数量取消 | 同属事件 1，首参数 `-1` | `C89ECA` 的 `-1/-2` 分支在 `C89FD2` 清理 agent 数量窗 ID，不调用开始制作 |
| 简易进度窗口 | 接收者 `imageBase + 0x28B6760`，事件 0 | `EA4F20..EA4F47` 创建 SynthesisSimple（90）；只比较指针，不解引用隐藏结构 |
| 简易取消/关闭 | 自有进度窗，callback `-1` | `E8A330` 分支；未完成时 `E8A3BB` 下发取消动作 `0x500` |

标准制作校验 `RecipeNote.CraftingRecipeId`。简易开始不写该字段，因此简易严格校验 `RecipeNote.ActiveCraftRecipeId`，不允许残留的标准配方 ID 兜底。简易进度额外检查 `[7]` 产物 ID（`EA4E3E/EA4E67`）和 `[3]/[4]` 当前/总次数；没有把仅有旧注释支持的成功/失败计数索引作为授权条件。标准完成使用公开 Synthesis 进度节点、实际结束与精确产出变化三者共同核实。

公开来源：[FFCS RecipeNote](https://github.com/Dalamud-DailyRoutines/FFXIVClientStructs/blob/87d51a788ee33329f20b06243be332dd4d35b13c/FFXIVClientStructs/FFXIV/Client/Game/UI/RecipeNote.cs)、[FFCS AddonSynthesis](https://github.com/Dalamud-DailyRoutines/FFXIVClientStructs/blob/87d51a788ee33329f20b06243be332dd4d35b13c/FFXIVClientStructs/FFXIV/Client/UI/AddonSynthesis.cs)、[Artisan Operations](https://github.com/PunishXIV/Artisan/blob/e4380555cac967ec47f976f1b23595a1efbbde73/Artisan/GameInterop/Operations.cs)。Artisan 单参数 callback 6 不作为安全刷新协议使用，因为本机接收函数实际读取第二个参数。

## 验证范围

新增纯逻辑项目 `tests/CraftSession.Tests` 覆盖材料不足、NQ/HQ 分配、重复材料保留、逐份 HQ 回应、实际配方字段隔离、99 次批次限制、三信号所有顺序、失败不补次数、超量拒绝、延迟冻结、同帧与 100 毫秒限流、长人为延迟及取消待发送动作。构建及静态审查不等于实机验收。本轮未发送真实制作、取消或材料转移操作，仍需在游戏中验证正常生命周期、Artisan 自动模式实际执行和网络结算时序。
