# FF14 独立插件

同一仓库维护四个分别安装、分别配置的国服 Dalamud 插件。**雇员工坊 / RetainerWorkshop 0.1.8.0** 统一原制作桥接与直售功能，并增加日／周累计和轮次停机。本版支持读取当前背包后直接勾选出售物品，自动保存名单；空名单也能显示背包内容。BatchTrade 0.1.5.0、RetainerCraftBridge 0.1.4.0 和 RetainerVendorSweep 0.1.6.0 保留各自项目与配置，不会被新插件覆盖。可下载版本以自定义仓库索引和对应 GitHub Release 为准。

**目标环境为国服 Soil / Dalamud API 15、.NET 10。离线编译和逻辑测试不代表真实客户端验收。** 游戏更新、插件更新后的接口兼容性需要重新核对。无需替换 DailyRoutines、AutoRetainer、Artisan 或 Lifestream 的 DLL。

原生回调适配限定国服游戏版本 `2026.10.06.0000.0000`；版本不符时拒绝执行，需先更新适配。RetainerWorkshop 使用同一固定基准，完整整合流程尚未实机验收。

| 插件 | 命令 | 功能 | 运行依赖 |
| --- | --- | --- | --- |
| 批量物品交易 / BatchTrade | `/batchtrade` | 当前角色向选中的玩家连续赠送一种指定物品，对方逐笔手动确认 | Dalamud API 15；不依赖 DailyRoutines |
| 雇员收取后制作 / RetainerCraftBridge | `/retainercraft` | AutoRetainer 轮转至指定制作角色并收完雇员后制作；开头角色雇员再次到期时停止制作并恢复收取 | AutoRetainer、Artisan、Lifestream |
| 多角色雇员直售 / RetainerVendorSweep | `/retainersell` | 手动启动，通过附近雇员铃直售角色背包物品；白名单保留，所选角色处理一遍即停 | AutoRetainer、Lifestream |
| 雇员工坊 / RetainerWorkshop | `/workshop` | 统一收取、可选制作和角色背包直售；日／周完成 N 轮自动出售一次，本次完成 M 轮后收尾待机 | AutoRetainer、Lifestream；启用制作时另需 Artisan |

**使用 RetainerWorkshop 前须停用 RetainerCraftBridge 和 RetainerVendorSweep。** 旧两个插件保持安装但关闭窗口不足以满足独占要求。BatchTrade 保持独立，不属于此次整合；不要在雇员工坊运行时同时启动交易或其他会操作背包、雇员、制作和换角的流程。

## 在另一台电脑安装

1. 使用与上述 API 15 兼容的国服 XIVLauncherCN / Soil。
2. 打开 Dalamud 设置，在“实验性”中的“自定义插件仓库”添加并保存：

   ```text
   https://raw.githubusercontent.com/Houraiji9/FFXIV-StandalonePlugins/main/pluginmaster.json
   ```

3. 刷新插件安装器，按需搜索“批量物品交易”“雇员收取后制作”“多角色雇员直售”或“雇员工坊”。安装器提供的版本与下载地址由索引决定；也可在 GitHub Releases 核对对应版本和摘要。
4. 用上表命令打开配置。所有插件加载后都不自动开始任务；雇员工坊的制作阶段和日／周自动直售也默认关闭，需自行配置后手动开始。

安装包见 [GitHub Releases](https://github.com/Houraiji9/FFXIV-StandalonePlugins/releases)。本仓库不分发原版 AutoRetainer、Artisan、Lifestream、DailyRoutines 或 Dalamud 本体，请从各自原有渠道安装依赖。另一台电脑需自行配置角色和物品；本仓库不上传、同步任何个人配置。

## 使用说明

- [批量交易：选物品、锁定目标、停止与结算](plugins/BatchTrade/README.md)
- [雇员后制作：循环交接、配方次数与依赖检查](plugins/RetainerCraftBridge/README.md)
- [多角色雇员直售：保留白名单、附近铃与单次遍历](plugins/RetainerVendorSweep/README.md)
- [雇员工坊：统一调度、日周累计、轮次停机与旧配置导入](plugins/RetainerWorkshop/README.md)
- [接口与第三方许可来源](THIRD-PARTY-NOTICES.md)
- [验证范围与客户端验收记录](VALIDATION.md)

批量交易只进行单向赠送，双方金币必须为零；对方放入物品、取消或出现不明结算时停止。总数量、HQ/NQ、接收玩家、每格数量上限及各步骤延迟均在开始前确定。**每格上限可填 1–999，每笔最多五格，整笔合计可以超过 999。** 按背包堆数量从大到小取物品；例如背包有 518、999、999 三堆，总量设为 2516、每格上限 999 时，按 **999 + 999 + 518 在一笔交易中完成放入**。每格对应一个独立背包堆，并同时遵守物品自身的堆叠上限；五格不足时，剩余数量留到下一笔。每个操作串行等待前一步的游戏状态，再按该步骤设置的延迟执行。

0.1.5 增加 **按组数 / 按个数** 两种总量输入，分别保留数值。每组个数自动读取物品堆叠上限：10 组分别为 9,990、990 或 10 个（上限 999、99 或 1）。界面显示换算后的总个数；手动每格上限只控制分装，不改变组的大小。旧配置继续按个数，总量范围仍为 1–10,000,000 个，组数换算超限时阻止开始。

雇员后制作以角色作为循环的起止标记。**开头角色、制作角色、配方和次数都在新插件界面配置**。AutoRetainer 继续按自己的顺序轮换；到达制作角色并收完该角色雇员后，桥接器关闭多角色模式并启动制作。开头角色的已选雇员再次到期时，桥接器停止自己启动的制作，等当前制作动作和界面结束，再恢复多角色收取。它不依赖 AutoRetainer 判断所有角色是否完成一轮。

启动前关闭 AutoRetainer 多角色模式，将范围设为 **Retainers（仅雇员）**：`/ays settings → 高级设置 → 专家设置 → 显示多角色模式类型选择器`，再回 `/ays` 主窗口选择。停止其他会同时操控换号、背包或制作的流程。桥接器通过公开接口切换运行开关，不直接编辑原插件配置文件；AR 自行管理运行状态保存。角色与雇员选择仍由 AR 决定，制作角色应安排在希望结束收取的位置。

0.1.4 可按配方保存**标准制作、简易制作、跟随 Artisan 设置**三种方式，并可在制作前从本角色雇员取出缺少的材料和水晶。明确的标准/简易方式由桥接器安排次数，须让制作角色处于正确职业；标准制作要求 Artisan 已开启自动模式，沿用其求解器/宏。取料与制作各步骤都有独立延迟。停止按钮移到窗口顶部并在清理期间保留；停止后不再提交后续动作，已发送请求需等待结果与界面收尾。

RetainerVendorSweep **仅出售角色背包物品，白名单代表保留不售**，不出售雇员仓库物品。角色从 AutoRetainer 已知资料中选择，并非自动发现账号下全部角色；每个角色需要已在可交互的雇员铃附近，不提供寻路或传送到铃。用户手动开始后只遍历所选角色一次，每步延迟可调；`/retainersell stop` 请求停止。既有实现及固定客户端静态证据已保留，真实出售、多角色和停止流程仍需客户端验收。

### 雇员工坊 0.1.7.0

工坊分别配置**开头角色、收尾角色和制作角色**。一轮是“从开头启动 AR，到收尾角色本次目标雇员收取确认且界面释放”的业务段，不等于全账号所有角色全部收齐；中间顺序仍由 AR 决定。原有标准／简易／兼容制作、可选雇员取料和整堆直售引擎编译进新独立程序集，不依赖旧两个插件 DLL。

日／周规则在所选时区及重置时间内累计已确认轮次，达到 N 后最多登记一次自动出售。例如每日 N = 5，达到当日第 5 轮触发一次，继续到第 10 轮不会再次触发。本次运行上限 M 单独从手动开始计数：达到 M 后先完成已经触发或暂停的出售任务，再整体待机；M = 0 关闭该上限。它不是定时启动插件的功能，重载也不会续跑旧队列。

出售时开头再次到期，先完成当前角色及界面收尾，再保存下一角色位置并返回收取；下一收尾边界恢复同一出售任务。每次恢复至少处理一个角色，避免一直到期却没有出售进展。用户停止则撤销全部后续阶段，未知在途结果保留阻塞，不自动重试。

开始前停用旧两个插件，并将 AR 有／无可用探险时的普通开铃操作均设为 **Disable_AutoRetainer**。这两项可以长期保持：AR 自身多角色自动开铃走独立分支，工坊取料／出售开铃需要防止自动收取争用。制作可选关闭；启用时才要求 Artisan，制作角色须已处于正确职业。各阶段延迟独立配置，工坊的轮换边界延迟不改变 AR 内部步骤速度。

旧配置支持先预览再应用。导入的保留名单与新插件已有保护合并，保护缺失或损坏时拒绝导入；不带入旧 `Enabled`，不自动启动，也不覆盖新设置的收尾角色、周期规则和运行上限。完整操作说明见[雇员工坊 README](plugins/RetainerWorkshop/README.md)，停止命令为 `/workshop stop`。

## 开发与构建

Windows、PowerShell 7、.NET SDK 10.0.100（或同功能带的补丁版本）。引用来自固定的 [Soil 26-11-03-01](https://github.com/Dalamud-DailyRoutines/Dalamud/releases/tag/26-11-03-01)，下载后校验 SHA-256；不依赖开发者的本地游戏目录。

```powershell
./scripts/Get-DalamudSdk.ps1
./scripts/Build.ps1
./scripts/Package.ps1 -Tag v0.1.5 -Plugins @('BatchTrade')
```

上述打包示例输出在 `artifacts/v0.1.5/`。只打包所选择的插件，合并索引时保留其他插件已发布的版本和下载地址。非空输出目录不会被覆盖，可用 `-OutputDirectory` 指定新的目录。插件 ZIP 仅包含本插件 DLL、清单、说明及许可证，不含游戏或其他插件二进制。也可在构建时传 `-DalamudLibPath` 使用相同 API 的本地引用。

构建脚本覆盖四个插件及全部配套检查，也可以单独构建与检查雇员工坊：

```powershell
dotnet build plugins/RetainerWorkshop/RetainerWorkshop.csproj -c Release
dotnet run --project tests/RetainerWorkshop.Ledger.Tests/RetainerWorkshop.Ledger.Tests.csproj -c Release
dotnet run --project tests/RetainerWorkshop.Sessions.Tests/RetainerWorkshop.Sessions.Tests.csproj -c Release
dotnet run --project tests/RetainerWorkshop.Config.Tests/RetainerWorkshop.Config.Tests.csproj -c Release
dotnet run --project tests/RetainerWorkshop.Tests/RetainerWorkshop.Tests.csproj -c Release
```

2026-11-05 作者完整本地回归中，四个插件 Release 构建均为 0 警告、0 错误，**1030 项检查通过**：工坊 423 项（104 账本、89 会话、107 配置与背包、123 总调度），既有插件及引擎 607 项。证据与验证边界见 [VALIDATION.md](VALIDATION.md)。仅选新插件的包装须保留三个现有插件的完整索引条目，不重发旧包；独立审查、源码提交、Release、根索引和实际游戏验收各自以对应记录为准。

GitHub Actions 在推送时执行构建和测试，根据 manifest 与已发布索引的版本差异选择要打包的插件。发布流程是：先验证构建产物，上传版本 Release 及 ZIP，然后将已验证 URL 对应的 `pluginmaster.json` 更新到主分支。源码推送与 Release 分发是独立步骤。

## 许可

项目采用 AGPL-3.0，第三方 MIT 许可及来源见 `licenses/` 和 `THIRD-PARTY-NOTICES.md`。此仓库不是上述依赖插件的官方发行版。
