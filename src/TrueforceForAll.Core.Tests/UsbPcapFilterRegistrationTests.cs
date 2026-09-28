// The USB class UpperFilters read that tells a machine which never loads
// USBPcap's capture driver apart from one that loads it and is refused
// (issue #44). The repairs differ, and one of them makes the obvious repair a
// no-op, so the classification has to be exact about the empty case: the
// reporter's machine had the value PRESENT but EMPTY, which is still "Windows
// has nothing to load".

using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    public class UsbPcapFilterRegistrationTests
    {
        [Fact]
        public void Registered_when_usbpcap_is_the_only_filter()
        {
            // What a healthy machine reads.
            Assert.Equal(UsbPcapFilterState.Registered,
                UsbPcapFilterRegistration.Classify(new[] { "USBPcap" }));
        }

        [Fact]
        public void Registered_when_usbpcap_sits_alongside_other_filters()
        {
            // Vendor USB stacks legitimately add their own; order is not ours.
            Assert.Equal(UsbPcapFilterState.Registered,
                UsbPcapFilterRegistration.Classify(new[] { "SomeVendorFilter", "USBPcap" }));
        }

        [Fact]
        public void Registered_ignores_case_and_padding()
        {
            // The value is written by an INF, not by us; do not be brittle.
            Assert.Equal(UsbPcapFilterState.Registered,
                UsbPcapFilterRegistration.Classify(new[] { "  usbpcap " }));
        }

        [Fact]
        public void Not_registered_when_the_value_is_present_but_empty()
        {
            // Issue #44's exact footprint: the value exists, holds nothing.
            Assert.Equal(UsbPcapFilterState.NotRegistered,
                UsbPcapFilterRegistration.Classify(new string[0]));
        }

        [Fact]
        public void Not_registered_when_the_value_is_absent()
        {
            Assert.Equal(UsbPcapFilterState.NotRegistered,
                UsbPcapFilterRegistration.Classify(null));
        }

        [Fact]
        public void Not_registered_when_only_blank_entries_remain()
        {
            // A multi-sz stripped down to padding is not a registration.
            Assert.Equal(UsbPcapFilterState.NotRegistered,
                UsbPcapFilterRegistration.Classify(new[] { "", "   " }));
        }

        [Fact]
        public void Not_registered_when_other_filters_are_present_without_usbpcap()
        {
            Assert.Equal(UsbPcapFilterState.NotRegistered,
                UsbPcapFilterRegistration.Classify(new[] { "SomeVendorFilter" }));
        }

        [Fact]
        public void A_substring_match_does_not_count_as_registered()
        {
            // "USBPcapSomething" is a different driver, not ours.
            Assert.Equal(UsbPcapFilterState.NotRegistered,
                UsbPcapFilterRegistration.Classify(new[] { "USBPcapSomething" }));
        }
    }
}
