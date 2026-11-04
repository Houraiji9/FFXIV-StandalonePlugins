using System.Diagnostics;
using System.Security.Cryptography;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace RetainerVendorSweep;

// The sole image-relative address is a pointer comparison for the inspected global YesNoProxy.
// Never calls a private function or reads an unpublished field. See docs/retainer-vendor-native.md.
internal sealed unsafe class VendorNativeCompatibility
{
    internal const string Version = "2026.10.06.0000.0000";
    internal const string ExecutableSha256 = "958B78E278AD896493C484EB313FF605AB41EDEFEF74F92B0EE078E69B281BF9";
    private readonly IDataManager data;
    private bool checkedImage;
    private string? imageError;
    internal nint SaleConfirmationReceiver { get; private set; }

    internal VendorNativeCompatibility(IDataManager data) => this.data = data;
    internal string? Check()
    {
        if(!data.GameData.Repositories.TryGetValue("ffxiv", out var repository) || repository.Version?.Trim() != Version)
            return $"直售仅适配客户端 {Version}，其他版本禁止原生操作。";
        if(sizeof(InventoryManager) != 0x3730 || sizeof(InventoryItem) != 0x48 || sizeof(AgentRetainer) != 0x68D0 ||
           sizeof(RetainerManager) != 0x310 || sizeof(ShopEventHandler) != 0x3298 || sizeof(ShopEventHandler.YesNoProxy) != 0x30)
            return "当前原生结构布局不匹配，禁止直售。";
        if(!checkedImage)
        {
            checkedImage = true;
            try
            {
                using var process = Process.GetCurrentProcess();
                var module = process.MainModule ?? throw new InvalidOperationException("Game module unavailable.");
                if(!string.Equals(Path.GetFileName(module.FileName), "ffxiv_dx11.exe", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Unexpected host executable.");
                using var file = File.OpenRead(module.FileName);
                if(Convert.ToHexString(SHA256.HashData(file)) != ExecutableSha256 || module.ModuleMemorySize <= 0x28C6F60)
                    throw new InvalidOperationException("Game executable does not match the inspected build.");
                SaleConfirmationReceiver = module.BaseAddress + 0x28C6F30;
                // File SHA alone does not establish that the mapped receiver still has the inspected vtable.
                if(*(nint*)SaleConfirmationReceiver != module.BaseAddress + 0x22CFA90)
                    throw new InvalidOperationException("Shop confirmation receiver vtable differs.");
            }
            catch(Exception ex) { imageError = $"游戏本体未通过固定版本校验（{ex.GetType().Name}），禁止直售。"; }
        }
        return imageError;
    }
}
