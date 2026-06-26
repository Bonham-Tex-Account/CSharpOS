using CSharpOS;

namespace OSTests;

/// <summary>
/// Covers the first-class device table: on-demand character-device creation,
/// explicit registration (used by the disk effort for a block device), the
/// reserved DiskDeviceId, and a Device's input/waiters/output-busy state.
/// </summary>
public class DeviceTableTests
{
    private static Hardware BareHardware()
    {
        return Test.NewHardware(512, new FakeOS());
    }

    [Fact]
    public void GetDevice_CreatesCharacterDeviceOnFirstUse()
    {
        Hardware hw = BareHardware();
        Device device = hw.GetDevice(3);
        Assert.Equal(3, device.Id);
        Assert.Equal(DeviceType.Character, device.Type);
        Assert.Empty(device.Input);
        Assert.Empty(device.Waiters);
        Assert.False(device.OutputBusy);
    }

    [Fact]
    public void GetDevice_ReturnsTheSameInstanceForTheSameId()
    {
        Hardware hw = BareHardware();
        Device first = hw.GetDevice(2);
        first.Input.Enqueue(99);
        first.Waiters.Add(7);
        first.OutputBusy = true;

        Device second = hw.GetDevice(2);
        Assert.Same(first, second);
        Assert.Equal(99, second.Input.Peek());
        Assert.Contains(7, second.Waiters);
        Assert.True(second.OutputBusy);
    }

    [Fact]
    public void GetDevice_DistinctIdsAreIndependentDevices()
    {
        Hardware hw = BareHardware();
        Device a = hw.GetDevice(0);
        Device b = hw.GetDevice(1);
        Assert.NotSame(a, b);

        a.Input.Enqueue(5);
        Assert.Empty(b.Input);
    }

    [Fact]
    public void RegisterDevice_StoresAndReturnsTheGivenDevice()
    {
        Hardware hw = BareHardware();
        Device block = new Device(Hardware.DiskDeviceId, DeviceType.Block);
        hw.RegisterDevice(block);

        Device fetched = hw.GetDevice(Hardware.DiskDeviceId);
        Assert.Same(block, fetched);
        Assert.Equal(DeviceType.Block, fetched.Type);
    }

    [Fact]
    public void DiskDeviceId_IsOutsideTheProcessIndexRange()
    {
        Assert.True(Hardware.DiskDeviceId >= OsLayout.MaxProcesses);
    }

    [Fact]
    public void Device_RoundTripsInputWaitersAndOutputBusy()
    {
        Device device = new Device(4, DeviceType.Character);
        device.Input.Enqueue(1);
        device.Input.Enqueue(2);
        device.Waiters.Add(0);
        device.Waiters.Add(3);
        device.OutputBusy = true;

        Assert.Equal(1, device.Input.Dequeue());
        Assert.Equal(2, device.Input.Dequeue());
        Assert.Equal(new List<int> { 0, 3 }, device.Waiters);
        Assert.True(device.OutputBusy);
    }
}
