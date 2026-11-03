# FF14 独立插件

两个分别安装、分别配置的国服 Dalamud 插件。源码和安装包放在同一个仓库，安装时可只选其中一个。无需替换 DailyRoutines、AutoRetainer 或 Artisan 的 DLL。

**当前 BatchTrade 为 0.1.3、RetainerCraftBridge 为 0.1.0，均为预发布候选：目标环境为国服 Soil / Dalamud API 15、.NET 10。离线编译和逻辑测试不代表真实客户端验收。** 游戏更新、插件更新后的接口兼容性需要重新核对。

BatchTrade 0.1.3 的交易回调适配限定国服游戏版本 `2026.10.06.0000.0000`；版本不符时拒绝执行原生交易操作，需先更新适配。

| 插件 | 命令 | 功能 | 运行依赖 |
| --- | --- | --- | --- |
| 批量物品交易 / BatchTrade | `/batchtrade` | 当前角色向选中的玩家连续赠送一种指定物品，对方逐笔手动确认 | Dalamud API 15；不依赖 DailyRoutines |
| 雇员收取后制作 / RetainerCraftBridge | `/retainercraft` | AutoRetainer 轮转至指定制作角色并收完雇员后制作；开头角色雇员再次到期时停止制作并恢复收取 | AutoRetainer、Artisan、Lifestream |

## 在另一台电脑安装

1. 使用与上述 API 15 兼容的国服 XIVLauncherCN / Soil。
2. 打开 Dalamud 设置，在“实验性”中的“自定义插件仓库”添加并保存：

   ```text
   https://raw.githubusercontent.com/Houraiji9/FFXIV-StandalonePlugins/main/pluginmaster.json
   ```

3. 刷新插件安装器，搜索“批量物品交易”或“雇员收取后制作”，分别安装。
4. 用上表命令打开配置。两个插件默认不运行自动任务，必须在界面主动开始。

安装包见 [GitHub Releases](https://github.com/Houraiji9/FFXIV-StandalonePlugins/releases)。本仓库不分发原版 AutoRetainer、Artisan、Lifestream、DailyRoutines 或 Dalamud 本体，请从各自原有渠道安装依赖。另一台电脑需自行配置角色和物品；本仓库不上传、同步任何个人配置。

## 使用说明

- [批量交易：选物品、锁定目标、停止与结算](plugins/BatchTrade/README.md)
- [雇员后制作：循环交接、配方次数与依赖检查](plugins/RetainerCraftBridge/README.md)
- [接口与第三方许可来源](THIRD-PARTY-NOTICES.md)
- [验证范围与客户端验收记录](VALIDATION.md)

批量交易只进行单向赠送，双方金币必须为零；对方放入物品、取消或出现不明结算时停止。总数量、HQ/NQ、接收玩家、每格数量上限及各步骤延迟均在开始前确定。**每格上限可填 1–999，每笔最多五格，整笔合计可以超过 999。** 按背包堆数量从大到小取物品；例如背包有 518、999、999 三堆，总量设为 2516、每格上限 999 时，按 **999 + 999 + 518 在一笔交易中完成放入**。每格对应一个独立背包堆，并同时遵守物品自身的堆叠上限；五格不足时，剩余数量留到下一笔。每个操作串行等待前一步的游戏状态，再按该步骤设置的延迟执行。

雇员后制作以角色作为循环的起止标记。**开头角色、制作角色、配方和次数都在新插件界面配置**。AutoRetainer 继续按自己的顺序轮换；到达制作角色并收完该角色雇员后，桥接器关闭多角色模式并启动制作。开头角色的已选雇员再次到期时，桥接器停止自己启动的制作，等当前制作动作和界面结束，再恢复多角色收取。它不依赖 AutoRetainer 判断所有角色是否完成一轮。

启动前关闭 AutoRetainer 多角色模式、将其范围设为仅雇员，并停止其他会同时操控换号、背包或制作的流程。桥接器通过公开接口切换运行开关；这些开关由原插件管理，原插件可能自行保存其状态，新插件不直接编辑原配置文件。AutoRetainer 的角色与雇员选择仍由原设置决定，制作角色应安排在希望结束收取的位置。制作次数指每个制作时段提交的配方执行次数，产出数量取决于配方；开头角色提前到期时，制作可在达到该次数前停止。Artisan 沿用其现有制作模式及设置。

## 开发与构建

Windows、PowerShell 7、.NET SDK 10.0.100（或同功能带的补丁版本）。引用来自固定的 [Soil 26-11-03-01](https://github.com/Dalamud-DailyRoutines/Dalamud/releases/tag/26-11-03-01)，下载后校验 SHA-256；不依赖开发者的本地游戏目录。

```powershell
./scripts/Get-DalamudSdk.ps1
./scripts/Build.ps1
./scripts/Package.ps1 -Tag v0.1.3 -Plugins @('BatchTrade')
```

本次输出在 `artifacts/v0.1.3/`。只打包所选择的插件，合并索引时保留其他插件已发布的版本和下载地址。非空输出目录不会被覆盖，可用 `-OutputDirectory` 指定新的目录。插件 ZIP 仅包含本插件 DLL、清单、说明及许可证，不含游戏或其他插件二进制。也可在构建时传 `-DalamudLibPath` 使用相同 API 的本地引用。

GitHub Actions 在推送时执行构建和测试，根据 manifest 与已发布索引的版本差异选择要打包的插件。发布流程是：先验证构建产物，上传版本 Release 及 ZIP，然后将已验证 URL 对应的 `pluginmaster.json` 更新到主分支。源码推送与 Release 分发是独立步骤。

## 许可

项目采用 AGPL-3.0，第三方 MIT 许可及来源见 `licenses/` 和 `THIRD-PARTY-NOTICES.md`。此仓库不是上述依赖插件的官方发行版。
