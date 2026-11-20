using System.Buffers.Binary;
using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class WindowsBootIdentityTests
{
    [Fact]
    public void SameBootEntryAcrossRebootsHasDifferentIdentityButSameKernelSessionIsStable()
    {
        var entry = Guid.NewGuid();
        var first = DateTimeOffset.Parse("2026-11-09T00:00:00Z").UtcDateTime.ToFileTimeUtc();
        var second = first + TimeSpan.FromMinutes(10).Ticks;
        Assert.Equal(SystemBootSession.WindowsIdentity(entry, first), SystemBootSession.WindowsIdentity(entry, first));
        Assert.NotEqual(SystemBootSession.WindowsIdentity(entry, first), SystemBootSession.WindowsIdentity(entry, second));
        Assert.NotEqual(SystemBootSession.WindowsIdentity(entry, first), SystemBootSession.WindowsIdentity(Guid.NewGuid(), first));
        Assert.Throws<InvalidDataException>(() => SystemBootSession.WindowsIdentity(entry, 0));
        Assert.Throws<InvalidDataException>(() => SystemBootSession.WindowsIdentity(Guid.Empty, first));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void SystemProcessTimeComesFromPidFourRatherThanFirstOrCallerProcess(int pointerSize)
    {
        var bytes = Processes(pointerSize);
        Assert.Equal(123456789, SystemBootSession.ParseSystemStartTime(bytes, pointerSize));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void TruncatedMissingEmptyAndCyclicEntriesCannotCreateAnIdentity(int pointerSize)
    {
        var bytes = Processes(pointerSize);
        Assert.Throws<InvalidDataException>(() => SystemBootSession.ParseSystemStartTime(bytes[..30], pointerSize));
        var malformed = bytes.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(malformed, 1);
        Assert.Throws<InvalidDataException>(() => SystemBootSession.ParseSystemStartTime(malformed, pointerSize));
        var missing = bytes.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(missing, 0);
        Assert.Throws<InvalidDataException>(() => SystemBootSession.ParseSystemStartTime(missing, pointerSize));
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(96 + 32), 0);
        Assert.Throws<InvalidDataException>(() => SystemBootSession.ParseSystemStartTime(bytes, pointerSize));
    }

    private static byte[] Processes(int pointerSize)
    {
        var bytes = new byte[192]; var pid = pointerSize == 8 ? 80 : 68;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 96);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(pid), 1234);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(32), 999999999);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(96 + pid), 4);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(96 + 32), 123456789);
        return bytes;
    }
}
