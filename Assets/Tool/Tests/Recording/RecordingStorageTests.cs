using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using PurrNet.Recording;

public class RecordingStorageTests
{
    string _directory;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "purrrec-storage-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, true);
    }

    string Write(string name, string content)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(content));
        return path;
    }

    [Test]
    public void NewNamesAreRandomWithoutTheGameOrAnExtension()
    {
        string a = RecordingSession.NewFileName(), b = RecordingSession.NewFileName();
        Assert.That(a, Does.Match("^[0-9a-f]{32}$"));
        Assert.That(a, Is.Not.EqualTo(b));
        Assert.That(Path.GetExtension(a), Is.Empty);
        Assert.That(a, Does.Not.Contain(UnityEngine.Application.productName));
    }

    [Test]
    public void RecordingsWaitInTheTemporaryFolder()
    {
        Assert.That(RecordingSession.ResolveDirectory("Recordings"),
            Is.EqualTo(Path.Combine(UnityEngine.Application.temporaryCachePath, "Recordings")));
        Assert.That(RecordingSession.LegacyDirectory("Recordings"),
            Is.EqualTo(Path.Combine(UnityEngine.Application.persistentDataPath, "Recordings")));
        Assert.That(RecordingSession.ResolveDirectory(_directory), Is.EqualTo(_directory), "a full path is kept");
        Assert.That(RecordingSession.LegacyDirectory(_directory), Is.Null);
    }

    [Test]
    public void RecordingsAreKnownByTheirFirstBytes()
    {
        Assert.That(RecordingUploader.IsRecordingFile(Write("0123abcd", "PURRREC\0\u0001\0")), Is.True, "a new recording");
        Assert.That(RecordingUploader.IsRecordingFile(Write("old.purrrec", "PURRREC\0\u0001\0")), Is.True, "one an earlier version wrote");
        Assert.That(RecordingUploader.IsRecordingFile(Write("0123abcd.sent", "")), Is.False, "a marker");
        Assert.That(RecordingUploader.IsRecordingFile(Write("notes.txt", "PURRREC\0")), Is.False, "another kind of file");
        Assert.That(RecordingUploader.IsRecordingFile(Write("save", "{\"level\":3}")), Is.False, "something else without an extension");
        Assert.That(RecordingUploader.IsRecordingFile(Write("starting", "")), Is.True, "one whose writer has only just created it");
    }

    [TestCase("https://purrnet.dev/api/services/recordings", "https://purrnet.dev/api/services/data-sharing")]
    [TestCase("https://purrnet.dev/api/services/recordings/", "https://purrnet.dev/api/services/data-sharing")]
    [TestCase("https://purrnet.dev/api/services/data-sharing", "https://purrnet.dev/api/services/data-sharing")]
    [TestCase("http://localhost:5173/upload-here", "http://localhost:5173/api/services/data-sharing")]
    [TestCase("", null)]
    [TestCase("not a url", null)]
    public void PermissionIsAskedNextToTheRecordingsEndpoint(string endpoint, string expected) =>
        Assert.That(RecordingPermission.UrlFor(endpoint), Is.EqualTo(expected));

    [Test]
    public void PosesAreRoundedToAPowerOfTwo()
    {
        Assert.That(RecordingSession.GridFor(0.001f), Is.EqualTo(1024f));
        Assert.That(RecordingSession.GridFor(0.01f), Is.EqualTo(128f));
        Assert.That(RecordingSession.GridFor(1f), Is.EqualTo(1f));
        Assert.That(RecordingSession.GridFor(0f), Is.EqualTo(0f), "0 keeps poses exact");
    }
}
