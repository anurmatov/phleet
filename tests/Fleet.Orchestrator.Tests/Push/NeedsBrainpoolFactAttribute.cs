namespace Fleet.Orchestrator.Tests.Push;

/// <summary>
/// A test that needs a 256-bit curve other than P-256. It is skipped on macOS and nowhere else.
///
/// The property under test is that a key which imports, has the right size and is still not P-256 is
/// refused by name. That needs a platform able to hold such a key. Apple's cryptography offers P-256,
/// P-384 and P-521 only: it refuses to create a brainpool key and equally refuses to import one from
/// PEM, so there is no key the test could embed to reach that branch there. The decision is made by
/// operating system, not by probing for the curve, so a Linux build that lost the curve fails loudly
/// instead of quietly skipping.
/// </summary>
public sealed class NeedsBrainpoolFactAttribute : FactAttribute
{
    public NeedsBrainpoolFactAttribute()
    {
        if (OperatingSystem.IsMacOS())
            Skip = "macOS cryptography can neither create nor import brainpoolP256r1, the 256-bit curve this test "
                + "refuses by name. It runs on Linux: scripts/test-linux.sh tests/Fleet.Orchestrator.Tests";
    }
}
