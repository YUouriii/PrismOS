using PrismOS.Widgets;

namespace PrismOS.Tests;

public class AudioMeterTests
{
    [Fact]
    public void EnumeratorClsid_IsTheClassNotTheInterface()
    {
        // A95664D5… is IID_IMMDeviceEnumerator. Asking the class factory for that Guid fails with
        // REGDB_E_CLASSNOTREG on every machine, which left the live spectrum silently dead.
        Assert.Equal(new Guid("bcde0395-e52f-467c-8e3d-c4579291692e"), LoopbackMeter.EnumeratorClsid);
    }
}
