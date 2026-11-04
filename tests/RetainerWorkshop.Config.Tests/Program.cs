using System.Text.Json;
using System.Text.Json.Nodes;
using RetainerWorkshop;
using RetainerCraftBridge;
using RetainerVendorSweep;

var checks = 0;
void Check(bool success, string name) { if(!success) throw new Exception(name); checks++; }
void Throws(Action action, string name) { try { action(); } catch { checks++; return; } throw new Exception(name); }
string Edit(string json, Action<JsonObject> edit) { var root = JsonNode.Parse(json)!.AsObject(); edit(root); return root.ToJsonString(); }
string Bridge() => JsonSerializer.Serialize(new
{
    Version = 2, Enabled = true, FirstCharacterCid = 10UL, CraftCharacterCid = 30UL,
    RecipeId = 123, CraftCount = 7, RestockMaterials = true, ArtisanAutoModeConfirmed = true,
    MaterialDelays = new MaterialDelays { BellMs = 1234 }, CraftDelays = new CraftDelays { StartCraftMs = 2345 },
    RecipeModes = new Dictionary<int, CraftMode> { [123] = CraftMode.Quick, [456] = CraftMode.Standard }
});
string Vendor() => JsonSerializer.Serialize(new
{
    Version = 1, Rules = new SaleRules
    {
        ItemIds = [100, 101, 102], CategoryIds = [20], ProtectedItemIds = [101],
        CategoryProtectedItemIds = new() { [20] = [102], [999] = [103] }, IncludeNq = true, IncludeHq = true
    },
    Delays = new VendorDelays { ContextMs = 3456, QuantityMs = 777 }, CharacterOrder = new ulong[] { 30, 10, 20 },
    ExcludedCharacters = new ulong[] { 20 }
});
var defaults = new WorkshopConfiguration();
Check(defaults.Version == 1 && !defaults.CraftEnabled && !defaults.Schedule.Enabled && defaults.StopAfterRounds == 0, "no startup or feature enable defaults");
Check(defaults.ValidateShape() == null && defaults.Validate() != null, "defaults editable but not runnable");
Check(defaults.Validate(true) != null, "manual sale needs explicit rules");
var current = new WorkshopConfiguration
{
    FirstCharacterCid = 1, LastCharacterCid = 2, CraftCharacterCid = 3,
    RecipeId = 55, CraftCount = 4, StopAfterRounds = 8,
    RecipeModes = new() { [55] = CraftMode.Standard }, SaleCharacterOrder = [1, 2], ExcludedSaleCharacters = [2],
    Sales = new() { ItemIds = [90], ProtectedItemIds = [91], CategoryProtectedItemIds = new() { [21] = [92] } },
    Schedule = new() { Enabled = true, Mode = PeriodMode.Weekly, RoundsBeforeSale = 9, TimeZoneId = "UTC", ResetMinuteOfDay = 240 },
    RotationDelays = new() { RelogMs = 4321 }
};
Check(current.Validate() == null, "configured no-craft run validates");
var frozen = current.Freeze();
current.Sales.ProtectedItemIds.Add(93); current.Sales.CategoryProtectedItemIds[21].Add(94);
current.Sales.ItemIds.Add(99); current.RecipeModes[55] = CraftMode.Quick; current.SaleCharacterOrder.Reverse();
current.ExcludedSaleCharacters.Add(3); current.Schedule.RoundsBeforeSale = 10; current.RotationDelays.RelogMs = 5555;
current.MaterialDelays.BellMs = 2000; current.CraftDelays.StartCraftMs = 2000;
Check(!frozen.Sales.ProtectedItemIds.Contains(93) && !frozen.Sales.CategoryProtectedItemIds[21].Contains(94), "deep protection snapshot");
Check(!frozen.Sales.ItemIds.Contains(99) && frozen.RecipeModes[55] == CraftMode.Standard, "rules/modes isolated");
Check(frozen.SaleCharacterOrder.SequenceEqual(new ulong[] { 1, 2 }) && !frozen.ExcludedSaleCharacters.Contains(3), "role choices isolated");
Check(frozen.Schedule.RoundsBeforeSale == 9 && frozen.RotationDelays.RelogMs == 4321, "schedule/rotation isolated");
Check(frozen.MaterialDelays.BellMs == 800 && frozen.CraftDelays.StartCraftMs == 800, "native delays isolated");
var malformed = frozen.Freeze(); malformed.Sales.ProtectedItemIds = null!;
Check(malformed.ValidateShape() != null, "null protection rejected"); Throws(() => malformed.Freeze(), "cannot freeze lost protection");
malformed = frozen.Freeze(); malformed.Sales.CategoryProtectedItemIds[3] = null!;
Check(malformed.ValidateShape() != null, "null nested protection rejected");
malformed = frozen.Freeze(); malformed.SaleCharacterOrder.Add(1); Check(malformed.ValidateShape() != null, "duplicate role order rejected");
malformed = frozen.Freeze(); malformed.RecipeModes[55] = (CraftMode)42; Check(malformed.ValidateShape() != null, "invalid craft enum rejected");
malformed = frozen.Freeze(); malformed.StopAfterRounds = -1; Check(malformed.Validate() != null, "negative manual limit rejected");
malformed = frozen.Freeze(); malformed.CraftEnabled = true; malformed.ArtisanAutoModeConfirmed = false; Check(malformed.Validate() != null, "standard requires confirmation");
malformed.RecipeModes[55] = CraftMode.Quick; Check(malformed.Validate() == null, "quick does not demand standard auto mode");
malformed.CraftCount = 10000; Check(malformed.Validate() != null, "craft count bound");

Check(LegacyImport.TryPreview(current, Bridge(), Vendor(), out var preview, out var error), "combined preview " + error);
var imported = preview!.Configuration;
Check(imported.FirstCharacterCid == 10 && imported.CraftCharacterCid == 30 && imported.RecipeId == 123 && imported.CraftCount == 7, "bridge fields copied");
Check(!imported.CraftEnabled, "old Enabled ignored");
Check(imported.LastCharacterCid == current.LastCharacterCid && imported.StopAfterRounds == current.StopAfterRounds, "new boundaries preserved");
Check(imported.Schedule.RoundsBeforeSale == current.Schedule.RoundsBeforeSale && imported.RotationDelays.RelogMs == 5555, "new schedule/rotation preserved");
Check(imported.MaterialDelays.BellMs == 1234 && imported.CraftDelays.StartCraftMs == 2345 && imported.SaleDelays.ContextMs == 3456, "all legacy delays imported");
Check(imported.SaleDelays.QuantityMs == 777, "unused compatibility delay preserved");
Check(imported.Sales.ProtectedItemIds.IsSupersetOf(new uint[] { 91, 93, 101 }) && imported.Sales.CategoryProtectedItemIds[21].SetEquals(new uint[] { 92, 94 }), "current and old protections retained");
Check(imported.Sales.CategoryProtectedItemIds[999].Contains(103), "disabled category protection kept globally");
Check(imported.SaleCharacterOrder.SequenceEqual(new ulong[] { 30, 10, 20 }) && imported.ExcludedSaleCharacters.SetEquals(new ulong[] { 20 }), "vendor role ordering imported");
Check(imported.Sales.IncludeHq && imported.Sales.IncludeNq, "quality selections copied");
Check(current.RecipeId == 55 && current.Sales.ItemIds.Contains(90), "preview does not mutate current");
imported.Sales.ProtectedItemIds.Clear(); var lines = preview.Preview; lines[0] = "tampered";
Check(preview.Configuration.Sales.ProtectedItemIds.Contains(101) && preview.Preview[0] != "tampered", "result getters return copies");
current.LastCharacterCid = 99; current.CraftEnabled = true; current.StopAfterRounds = 17;
current.Schedule.RoundsBeforeSale = 33; current.RotationDelays.StartMs = 9000; current.Sales.ProtectedItemIds.Add(888);
var applied = LegacyImport.Apply(current, preview);
Check(applied.LastCharacterCid == 99 && applied.CraftEnabled && applied.StopAfterRounds == 17, "apply preserves post-preview new settings");
Check(applied.Schedule.RoundsBeforeSale == 33 && applied.RotationDelays.StartMs == 9000, "apply does not restore stale schedule");
Check(applied.Sales.ProtectedItemIds.Contains(888) && applied.Sales.ProtectedItemIds.Contains(101), "newly added protection not lost on apply");
Check(applied.RecipeId == 123 && applied.Sales.ItemIds.Contains(100), "apply uses previewed imported fields");
Check(LegacyImport.TryPreview(current, Bridge(), null, out var bridgeOnly, out _), "bridge-only accepted");
Check(bridgeOnly!.Configuration.Sales.ItemIds.SetEquals(current.Sales.ItemIds), "bridge-only preserves sales");
Check(LegacyImport.TryPreview(current, null, Vendor(), out var vendorOnly, out _), "vendor-only accepted");
Check(vendorOnly!.Configuration.RecipeId == current.RecipeId && vendorOnly.Configuration.RecipeModes[55] == CraftMode.Quick, "vendor-only preserves craft");

void Reject(string? bridge, string? vendor, string label)
{ Check(!LegacyImport.TryPreview(current, bridge, vendor, out var rejected, out var reason) && rejected == null && reason.Length > 0, label); }
Reject(null, null, "no source rejected"); Reject("", null, "empty file rejected"); Reject("[]", null, "array config rejected");
Reject("{\"Version\":2,\"Version\":2}", null, "duplicate property rejected");
Reject(Edit(Bridge(), x => x["Version"] = 3), null, "unknown bridge version rejected");
Reject(null, Edit(Vendor(), x => x["Version"] = 2), "unknown vendor version rejected");
foreach(var property in new[] { "ItemIds", "CategoryIds", "ProtectedItemIds", "CategoryProtectedItemIds" })
{
    Reject(null, Edit(Vendor(), x => x["Rules"]!.AsObject().Remove(property)), "missing collection " + property);
    Reject(null, Edit(Vendor(), x => x["Rules"]![property] = null), "null collection " + property);
}
Reject(null, Edit(Vendor(), x => x["Rules"]!["CategoryProtectedItemIds"]!["20"] = null), "null nested protection rejected");
Reject(null, Edit(Vendor(), x => x["Rules"]!["ProtectedItemIds"] = new JsonArray(0)), "zero protected id rejected");
Reject(null, Edit(Vendor(), x => x["Rules"]!["ProtectedItemIds"] = new JsonArray(5, 5)), "duplicate protected id rejected");
Reject(null, Edit(Vendor(), x => x["Rules"]!["ProtectedItemIds"] = new JsonArray(ulong.MaxValue)), "overflow item id rejected");
Reject(null, Edit(Vendor(), x => x["Rules"]!["IncludeHq"] = "false"), "string boolean rejected");
Reject(Edit(Bridge(), x => x["CraftCount"] = 1.5), null, "fractional count rejected");
Reject(Edit(Bridge(), x => x["CraftCount"] = 0), null, "zero count rejected");
Reject(Edit(Bridge(), x => x["RecipeModes"]!["123"] = 5), null, "unknown mode rejected");
Reject(Edit(Bridge(), x => x["RecipeModes"]!["0123"] = 1), null, "normalized duplicate recipe rejected");
Reject(null, Edit(Vendor(), x => x["CharacterOrder"] = new JsonArray(10, 10)), "duplicate character order rejected");
Reject(null, Edit(Vendor(), x => x["ExcludedCharacters"] = new JsonArray(-1)), "negative cid rejected");
Reject(null, Edit(Vendor(), x => x["Delays"]!["ContextMs"] = 0), "invalid imported delay not silently clamped");
Reject(null, Edit(Vendor(), x => x["Rules"]!["CategoryProtectedItemIds"]!["020"] = new JsonArray(44)), "normalized duplicate protection category rejected");
var metadata = Edit(Vendor(), x => { x["$type"] = "Never.Load.This.Type, MissingAssembly"; x["Rules"]!["$type"] = "ignored"; x["Rules"]!["CategoryProtectedItemIds"]!["$type"] = "ignored"; });
Check(LegacyImport.TryPreview(current, null, metadata, out _, out _), "inert Dalamud type metadata never resolved");
var namedModes = Edit(Bridge(), x => { x["RecipeModes"]!["123"] = "Quick"; x["RecipeModes"]!["$type"] = "ignored"; });
Check(LegacyImport.TryPreview(current, namedModes, null, out var named, out _) && named!.Configuration.RecipeModes[123] == CraftMode.Quick, "legacy named mode safe parsing");
Console.WriteLine($"RetainerWorkshop configuration/import: {checks} checks passed; no game or legacy plugin code executed.");
