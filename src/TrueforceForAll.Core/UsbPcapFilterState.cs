using System;

namespace TrueforceForAll.Core
{
    /// <summary>Whether USBPcap's capture driver is registered as an upper
    /// filter on the USB device class, which is what makes Windows load it.</summary>
    public enum UsbPcapFilterState
    {
        /// <summary>The registry could not be read, so we know nothing.</summary>
        Unknown = 0,
        /// <summary>"USBPcap" is in the class UpperFilters list: Windows will
        /// ask for the driver. If it still is not running, something is
        /// refusing to load it.</summary>
        Registered,
        /// <summary>The UpperFilters value is missing, empty, or lists other
        /// filters but not USBPcap. Windows never asks for the driver at all,
        /// so reinstalling over the top changes nothing: the registration has
        /// to be rebuilt by the vendor uninstaller plus a fresh install.</summary>
        NotRegistered,
    }

    /// <summary>
    /// Reads the USB device class UpperFilters list, the registration that
    /// decides whether Windows loads USBPcap's capture driver at all.
    ///
    /// Issue #44: a reporter's USBPcap was installed, its CLI on disk, and the
    /// service STOPPED, while UpperFilters EXISTED BUT WAS EMPTY. On a healthy
    /// machine it reads exactly ["USBPcap"]. Empty-but-present is the footprint
    /// of USBPcap.inf's DelReg (0x00018002, delete a string from a multi-sz);
    /// how it gets stripped is unknown, and nothing we ship touches it.
    ///
    /// This class is READ ONLY by design and must stay that way. A USB class
    /// upper filter whose driver Windows then refuses to load can stop the USB
    /// controllers starting, which costs the user their keyboard and mouse, so
    /// the registration is only ever rebuilt by USBPcap's own installer.
    /// </summary>
    public static class UsbPcapFilterRegistration
    {
        /// <summary>The USB device class GUID whose UpperFilters list carries
        /// the capture driver registration.</summary>
        public const string UsbDeviceClassGuid = "{36fc9e60-c465-11cf-8056-444553540000}";

        /// <summary>Registry path of the class key, under HKEY_LOCAL_MACHINE.</summary>
        public const string ClassKeyPath =
            @"SYSTEM\CurrentControlSet\Control\Class\" + UsbDeviceClassGuid;

        public const string ValueName = "UpperFilters";

        /// <summary>The filter name USBPcap registers itself under.</summary>
        public const string FilterName = "USBPcap";

        /// <summary>
        /// Classify an already-read UpperFilters multi-sz. Split out from the
        /// registry read so it can be tested without touching the machine.
        /// A null array means the value (or the key) was absent, which is still
        /// a definite "not registered": Windows has nothing to load. Pass
        /// <c>null</c> only for a genuine absence; a failed read is Unknown and
        /// is decided by the caller.
        /// </summary>
        public static UsbPcapFilterState Classify(string[] upperFilters)
        {
            if (upperFilters == null || upperFilters.Length == 0) return UsbPcapFilterState.NotRegistered;

            foreach (var entry in upperFilters)
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;
                if (string.Equals(entry.Trim(), FilterName, StringComparison.OrdinalIgnoreCase))
                    return UsbPcapFilterState.Registered;
            }

            // Non-empty, but USBPcap is not in it. Other filters may legitimately
            // be present (vendor USB stacks), so this is not a corruption signal
            // on its own, just an absent registration.
            return UsbPcapFilterState.NotRegistered;
        }
    }
}
