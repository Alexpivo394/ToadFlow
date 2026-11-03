using Shouldly;
using ToadFlow.Server.Clash;
using ToadFlow.Server.Clash.Collisions;
using ToadFlow.Server.Clash.Images;

namespace ToadFlow.Server.Tests;

/// <summary>Версия картинки — по геометрии пары, а не по прогону: ночной прогон не сбрасывает кеш картинок.</summary>
public class ImageVersionTests
{
    private static ClashCollision Collision(Guid run, long fingerprintA = 11, long fingerprintB = 22) => new()
    {
        Id = Guid.NewGuid(), PartId = Guid.NewGuid(), LastFoundRunId = run, ModelA = "a", UniqueIdA = "ua", ModelB = "b", UniqueIdB = "ub",
        FingerprintA = fingerprintA, FingerprintB = fingerprintB, X = 1, Y = 2, Z = 3
    };

    [Test]
    public void NightRun_SameFingerprints_SameImageVersion()
    {
        ImageTarget.VersionOf(Collision(Guid.NewGuid())).ShouldBe(ImageTarget.VersionOf(Collision(Guid.NewGuid())));

        // Строки без отпечатков (до 2.0) — как раньше, по прогону.
        ImageTarget.VersionOf(Collision(Guid.NewGuid(), 0, 0)).ShouldNotBe(ImageTarget.VersionOf(Collision(Guid.NewGuid(), 0, 0)));
    }

    [Test]
    public async Task FingerprintChanged_NewVersion_OldKeptUntilNewReady()
    {
        var run = Guid.NewGuid();
        var before = Collision(run);
        var after = Collision(run, fingerprintA: 12);
        var (v1, v2) = (ImageTarget.VersionOf(before), ImageTarget.VersionOf(after));
        v2.ShouldNotBe(v1);

        var root = Path.Combine(Path.GetTempPath(), "toadflow-image-versions-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (part, id) = (Guid.NewGuid(), Guid.NewGuid());
            await ClashStorage.WriteImageAsync(root, part, id, v1, new MemoryStream([1]), CancellationToken.None);
            // Пока новая не записана, отдаётся старая.
            File.Exists(ClashStorage.ImagePath(root, part, id, v1)).ShouldBeTrue();

            await ClashStorage.WriteImageAsync(root, part, id, v2, new MemoryStream([2]), CancellationToken.None);

            File.Exists(ClashStorage.ImagePath(root, part, id, v2)).ShouldBeTrue();
            File.Exists(ClashStorage.ImagePath(root, part, id, v1)).ShouldBeFalse();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
