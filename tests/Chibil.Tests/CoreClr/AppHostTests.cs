#nullable enable
using System;
using System.Text;
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class AppHostTests
{
    // The 64-byte ASCII placeholder embedded in every apphost template
    // (Microsoft.NET.HostModel AppBinaryPathPlaceholder).
    const string Placeholder =
        "c3ab8ff13720e8ad9047dd39466b3c8974e592c2fa383d4a3960714caef0c4f2";

    static byte[] FakeApphost(out int slotOffset)
    {
        // [16 bytes junk][64-byte placeholder][16 bytes junk]
        byte[] head = new byte[16];
        byte[] tail = new byte[16];
        for (int i = 0; i < 16; i++) { head[i] = 0xAA; tail[i] = 0xBB; }
        byte[] mid = Encoding.ASCII.GetBytes(Placeholder);
        slotOffset = head.Length;
        byte[] img = new byte[head.Length + mid.Length + tail.Length];
        Buffer.BlockCopy(head, 0, img, 0, head.Length);
        Buffer.BlockCopy(mid, 0, img, head.Length, mid.Length);
        Buffer.BlockCopy(tail, 0, img, head.Length + mid.Length, tail.Length);
        return img;
    }

    [Fact]
    public void Patcher_writes_dll_name_nul_terminated_and_zero_pads_the_slot()
    {
        byte[] img = FakeApphost(out int slot);
        bool ok = AppHostPatcher.Patch(img, "foo.dll");
        Assert.True(ok);

        byte[] name = Encoding.UTF8.GetBytes("foo.dll");
        for (int i = 0; i < name.Length; i++) Assert.Equal(name[i], img[slot + i]);
        Assert.Equal(0, img[slot + name.Length]);                 // NUL terminator
        for (int i = name.Length + 1; i < 64; i++)                // zero-padded to 64
            Assert.Equal(0, img[slot + i]);
        // Bytes outside the 64-byte slot are untouched.
        Assert.Equal(0xAA, img[slot - 1]);
        Assert.Equal(0xBB, img[slot + 64]);
        // The placeholder text is gone.
        Assert.DoesNotContain(Placeholder, Encoding.ASCII.GetString(img));
    }

    [Fact]
    public void Patcher_returns_false_when_no_placeholder_present()
    {
        byte[] img = new byte[128]; // all zeros, no sentinel
        Assert.False(AppHostPatcher.Patch(img, "foo.dll"));
    }

    [Fact]
    public void Patcher_returns_false_when_name_exceeds_slot()
    {
        byte[] img = FakeApphost(out _);
        string tooLong = new string('a', 60) + ".dll"; // 64 bytes, no room for NUL
        Assert.False(AppHostPatcher.Patch(img, tooLong));
    }

    [Fact]
    public void Locator_returns_null_for_empty_dotnet_root()
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "chibil_emptyroot_" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(root);
        try
        {
            Assert.Null(AppHostLocator.FindInRoot(root)); // no sdk/, no packs/ → not found, no throw
        }
        finally { try { System.IO.Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Locator_finds_highest_sdk_apphost_template()
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "chibil_fakeroot_" + Guid.NewGuid().ToString("N")[..8]);
        string exe = System.Runtime.InteropServices.RuntimeInformation
            .IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? "apphost.exe" : "apphost";
        string lowTpl  = System.IO.Path.Combine(root, "sdk", "9.0.100", "AppHostTemplate");
        string highTpl = System.IO.Path.Combine(root, "sdk", "10.0.100", "AppHostTemplate");
        System.IO.Directory.CreateDirectory(lowTpl);
        System.IO.Directory.CreateDirectory(highTpl);
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(lowTpl, exe), new byte[] { 1 });
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(highTpl, exe), new byte[] { 2 });
        try
        {
            string? found = AppHostLocator.FindInRoot(root);
            Assert.NotNull(found);
            Assert.Equal(System.IO.Path.Combine(highTpl, exe), found); // picks 10.0.100 over 9.0.100
        }
        finally { try { System.IO.Directory.Delete(root, true); } catch { } }
    }
}
