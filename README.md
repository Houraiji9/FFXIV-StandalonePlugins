# FF14 独立插件

同一仓库维护分别安装、分别配置的国服 Dalamud 插件。当前已发布 BatchTrade 与 RetainerCraftBridge；第三个插件 RetainerVendorSweep 正在开发，暂定版本 0.1.6.0，尚未写入安装索引或发布。无需替换 DailyRoutines、AutoRetainer 或 Artisan 的 DLL。

**当前 BatchTrade 为 0.1.5、RetainerCraftBridge 为 0.1.4 预发布候选：目标环境为国服 Soil / Dalamud API 15、.NET 10。离线编译和逻辑测试不代表真实客户端验收。** 游戏更新、插件更新后的接口兼容性需要重新核对。

两个已发布插件的原生回调适配限定国服游戏版本 `2026.10.06.0000.0000`；版本不符时拒绝执行，需先更新适配。RetainerVendorSweep 也以该版本为适配目标，原生研究和实现尚在进行，不能据此认定已兼容。

| 插件 | 命令 | 功能 | 运行依赖 |
| --- | --- | --- | --- |
| 批量物品交易 / BatchTrade | `/batchtrade` | 当前角色向选中的玩家连续赠送一种指定物品，对方逐笔手动确认 | Dalamud API 15；不依赖 DailyRoutines |
| 雇员收取后制作 / RetainerCraftBridge | `/retainercraft` | AutoRetainer 轮转至指定制作角色并收完雇员后制作；开头角色雇员再次到期时停止制作并恢复收取 | AutoRetainer、Artisan、Lifestream |
| 多角色雇员直售 / RetainerVendorSweep（开发中） | `/retainersell` | 手动启动，通过附近雇员铃直售角色背包物品；白名单保留，所选角色处理一遍即停 | 目标依赖 AutoRetainer、Lifestream；未发布 |

## 在另一台电脑安装

1. 使用与上述 API 15 兼容的国服 XIVLauncherCN / Soil。
2. 打开 Dalamud 设置，在“实验性”中的“自定义插件仓库”添加并保存：

   ```text
   https://raw.githubusercontent.com/Houraiji9/FFXIV-StandalonePlugins/main/pluginmaster.json
   ```

3. 刷新插件安装器，搜索“批量物品交易”或“雇员收取后制作”，分别安装。
4. 用上表命令打开配置。两个已发布插件默认不运行自动任务，必须在界面主动开始。RetainerVendorSweep 尚未进入上述安装索引。

安装包见 [GitHub Releases](https://github.com/Houraiji9/FFXIV-StandalonePlugins/releases)。本仓库不分发原版 AutoRetainer、Artisan、Lifestream、DailyRoutines 或 Dalamud 本体，请从各自原有渠道安装依赖。另一台电脑需自行配置角色和物品；本仓库不上传、同步任何个人配置。

## 使用说明

- [批量交易：选物品、锁定目标、停止与结算](plugins/BatchTrade/README.md)
- [雇员后制作：循环交接、配方次数与依赖检查](plugins/RetainerCraftBridge/README.md)
- [多角色雇员直售：保留白名单、附近铃与单次遍历（开发中）](plugins/RetainerVendorSweep/README.md)
- [接口与第三方许可来源](THIRD-PARTY-NOTICES.md)
- [验证范围与客户端验收记录](VALIDATION.md)

批量交易只进行单向赠送，双方金币必须为零；对方放入物品、取消或出现不明结算时停止。总数量、HQ/NQ、接收玩家、每格数量上限及各步骤延迟均在开始前确定。**每格上限可填 1–999，每笔最多五格，整笔合计可以超过 999。** 按背包堆数量从大到小取物品；例如背包有 518、999、999 三堆，总量设为 2516、每格上限 999 时，按 **999 + 999 + 518 在一笔交易中完成放入**。每格对应一个独立背包堆，并同时遵守物品自身的堆叠上限；五格不足时，剩余数量留到下一笔。每个操作串行等待前一步的游戏状态，再按该步骤设置的延迟执行。

0.1.5 增加 **按组数 / 按个数** 两种总量输入，分别保留数值。每组个数自动读取物品堆叠上限：10 组分别为 9,990、990 或 10 个（上限 999、99 或 1）。界面显示换算后的总个数；手动每格上限只控制分装，不改变组的大小。旧配置继续按个数，总量范围仍为 1–10,000,000 个，组数换算超限时阻止开始。

雇员后制作以角色作为循环的起止标记。**开头角色、制作角色、配方和次数都在新插件界面配置**。AutoRetainer 继续按自己的顺序轮换；到达制作角色并收完该角色雇员后，桥接器关闭多角色模式并启动制作。开头角色的已选雇员再次到期时，桥接器停止自己启动的制作，等当前制作动作和界面结束，再恢复多角色收取。它不依赖 AutoRetainer 判断所有角色是否完成一轮。

启动前关闭 AutoRetainer 多角色模式，将范围设为 **Retainers（仅雇员）**：`/ays settings → 高级设置 → 专家设置 → 显示多角色模式类型选择器`，再回 `/ays` 主窗口选择。停止其他会同时操控换号、背包或制作的流程。桥接器通过公开接口切换运行开关，不直接编辑原插件配置文件；AR 自行管理运行状态保存。角色与雇员选择仍由 AR 决定，制作角色应安排在希望结束收取的位置。

0.1.4 可按配方保存**标准制作、简易制作、跟随 Artisan 设置**三种方式，并可在制作前从本角色雇员取出缺少的材料和水晶。明确的标准/简易方式由桥接器安排次数，须让制作角色处于正确职业；标准制作要求 Artisan 已开启自动模式，沿用其求解器/宏。取料与制作各步骤都有独立延迟。停止按钮移到窗口顶部并在清理期间保留；停止后不再提交后续动作，已发送请求需等待结果与界面收尾。

RetainerVendorSweep 的本轮范围是**仅出售角色背包物品，白名单代表保留不售**，不出售雇员仓库物品。角色从 AutoRetainer 已知资料中选择，并非自动发现账号下全部角色；每个角色需要已在可交互的雇员铃附近，不提供寻路或传送到铃。用户手动开始后只遍历所选角色一次，每步延迟可调；`/retainersell stop` 请求停止。实现、原生接口证据与客户端验收尚未完成，不能把这段范围说明当作可运行证明。

## 开发与构建

Windows、PowerShell 7、.NET SDK 10.0.100（或同功能带的补丁版本）。引用来自固定的 [Soil 26-11-03-01](https://github.com/Dalamud-DailyRoutines/Dalamud/releases/tag/26-11-03-01)，下载后校验 SHA-256；不依赖开发者的本地游戏目录。

```powershell
./scripts/Get-DalamudSdk.ps1
./scripts/Build.ps1
./scripts/Package.ps1 -Tag v0.1.5 -Plugins @('BatchTrade')
```

本次输出在 `artifacts/v0.1.5/`。只打包所选择的插件，合并索引时保留其他插件已发布的版本和下载地址。非空输出目录不会被覆盖，可用 `-OutputDirectory` 指定新的目录。插件 ZIP 仅包含本插件 DLL、清单、说明及许可证，不含游戏或其他插件二进制。也可在构建时传 `-DalamudLibPath` 使用相同 API 的本地引用。

构建脚本已纳入 RetainerVendorSweep 及其独立规则测试。新插件完成实现并通过所需验证后，独立打包命令为 `./scripts/Package.ps1 -Tag v0.1.6 -Plugins @('RetainerVendorSweep')`；它只生成新插件 ZIP，并在输出索引中保留 BatchTrade 0.1.5.0、RetainerCraftBridge 0.1.4.0 的完整已发布条目。命令不修改根目录 `pluginmaster.json`，也不自动发布。暂定 tag 须在正式发布前再次确认未占用。

GitHub Actions 在推送时执行构建和测试，根据 manifest 与已发布索引的版本差异选择要打包的插件。发布流程是：先验证构建产物，上传版本 Release 及 ZIP，然后将已验证 URL 对应的 `pluginmaster.json` 更新到主分支。源码推送与 Release 分发是独立步骤。

## 许可

项目采用 AGPL-3.0，第三方 MIT 许可及来源见 `licenses/` 和 `THIRD-PARTY-NOTICES.md`。此仓库不是上述依赖插件的官方发行版。
