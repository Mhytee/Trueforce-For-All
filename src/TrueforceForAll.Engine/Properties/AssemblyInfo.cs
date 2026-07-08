using System.Runtime.CompilerServices;

// Lets the unit-test project reach internal members (e.g. ParsePacket,
// ResolveDashBase, the capture logs' FormatRow) so the pure parsing/synthesis
// logic can be tested without sockets or the host.
[assembly: InternalsVisibleTo("TrueforceForAll.Core.Tests")]
