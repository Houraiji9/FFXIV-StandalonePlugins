namespace RetainerVendorSweep;

// Pure rules used by the native path, extracted so boundary and late-response tests exercise production code.
public static class VendorNativeRules
{
    public static bool TryUnitPrice(uint basePrice, bool highQuality, int materiaCount, out uint price)
    {
        price = 0;
        if(basePrice == 0 || materiaCount is < 0 or > 5) return false;
        try
        {
            long value = basePrice;
            // Client 867FF0 applies each multiplier and rounding separately, not a combined multiplier.
            if(highQuality) value = checked((value * 11 + 9) / 10);
            if(materiaCount != 0) value = checked((value * (10 + materiaCount) + 9) / 10);
            if(value is <= 0 or > int.MaxValue) return false;
            price = (uint)value; return true;
        }
        catch(OverflowException) { return false; }
    }

    public static bool CancelResponseComplete(bool ownCancelSubmitted, bool ownWindowFinalized,
        bool startingSell, bool transactionPending, uint confirmationAddonId, bool bagsUnchanged, bool gilUnchanged)
        => ownCancelSubmitted && ownWindowFinalized && !startingSell && !transactionPending
           && confirmationAddonId == 0 && bagsUnchanged && gilUnchanged;
}
