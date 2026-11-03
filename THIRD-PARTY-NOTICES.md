# Third-party references and licenses

This repository is distributed under AGPL-3.0; see LICENSE. It does not bundle third-party plugin binaries, game data, Dalamud binaries, accounts, or user configuration.

## BatchTrade

- DailyRoutines.ModulesPublic, commit `4c56b564fb33b9ac8557e52f76c4ca60f706a8f3`, AGPL-3.0: [General/AutoSendMoney.cs](https://github.com/Dalamud-DailyRoutines/DailyRoutines.ModulesPublic/blob/4c56b564fb33b9ac8557e52f76c4ca60f706a8f3/General/AutoSendMoney.cs). Reference for trade UI invocation and callbacks. The new workflow limits interaction to one item and a locked recipient, using independent configuration and Dalamud services.
- QuickTransfer, commit `8b41c30c08deaedfbb87799bc3c92997a9755a56`, MIT: [source](https://github.com/Kagekazu/QuickTransfer/tree/8b41c30c08deaedfbb87799bc3c92997a9755a56). Reference for inventory context-menu and numeric input contracts. License retained in `licenses/QuickTransfer.LICENSE`.

## RetainerCraftBridge

- AutoRetainer installed compatibility baseline 4.6.0.8, source commit `d57cb55cf7c35096b9bc9761363896755999795c`: [CN fork](https://github.com/Ookura-Risona/AutoRetainer/tree/d57cb55cf7c35096b9bc9761363896755999795c). Used to inspect public IPC semantics; no original DLL is modified or redistributed.
- [AutoRetainerAPI](https://github.com/PunishXIV/AutoRetainerAPI/tree/a7eb8617c10efda34954dd169bb6cc9e60539e63), commit `a7eb8617c10efda34954dd169bb6cc9e60539e63`, MIT: public callgate names and minimal offline-data contract; license retained in `licenses/AutoRetainerAPI.LICENSE`. The bridge declares its own small DTOs and uses Dalamud's public callgates rather than linking the AutoRetainer plugin assembly.
- [Artisan](https://github.com/PunishXIV/Artisan/tree/e4380555cac967ec47f976f1b23595a1efbbde73), AGPL-3.0, installed compatibility baseline 4.0.5.21, source commit `e4380555cac967ec47f976f1b23595a1efbbde73`: public `Artisan.CraftItem(ushort, int)`, `SetEnduranceStatus(bool)` and status callgates. Native interoperability research also consulted `GameInterop/PreCrafting.cs`, `GameInterop/Operations.cs`, `IPC/RetainerInfo.cs` and `Tasks/TaskSelectRetainer.cs`. The new material and explicit-mode crafting services own their queues, window lifecycles, quantities and cancellation, with callback contracts independently checked against the pinned client. No Artisan plugin binary is bundled or modified; its internal queues are not invoked through reflection.
- [Lifestream](https://github.com/NightmareXIV/Lifestream/tree/52c54fad10daed135faf4e44ce53754fa5d3cb4b) installed compatibility baseline 2.6.0.5, source commit `52c54fad10daed135faf4e44ce53754fa5d3cb4b`: public character-change and busy/abort callgates. No Lifestream implementation or binary is bundled.

## Host references

- Dalamud Soil [26-11-03-01](https://github.com/Dalamud-DailyRoutines/Dalamud/releases/tag/26-11-03-01), commit `4a191c4072677489277f59141b795042c22436ad`, API 15. Build-only references remain provided by the host at runtime.
- [FFXIVClientStructs](https://github.com/Dalamud-DailyRoutines/FFXIVClientStructs/tree/87d51a788ee33329f20b06243be332dd4d35b13c), MIT, via the host. The plugins use the host's generated native bindings. BatchTrade 0.1.1 additionally identifies two embedded InventoryManager callback receivers using an explicitly version-gated layout verified against the CN `2026.10.06.0000.0000` executable; these private fields are not exposed by this FFCS revision. The adapter only checks receiver identity and event kind; it does not call a hand-written native function or add a byte signature. No executable bytes are redistributed.
- The SDK acquisition script downloads the official [7-Zip 26.05 standalone extractor](https://github.com/ip7z/7zip/releases/tag/26.05) for build use, with a pinned SHA-256. It is not redistributed in the plugin packages.

Upstream names and trademarks belong to their respective owners. Runtime dependency settings and ownership remain with their original plugins.
