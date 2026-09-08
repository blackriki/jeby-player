using EmbyPlayer.Core.Details;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Core.Tests;

[TestClass]
public sealed class SimilarMediaLoadResultTests
{
    [TestMethod]
    public void Success_AllowsAnEmptyResultWithoutAnError()
    {
        var result = SimilarMediaLoadResult.Success(Array.Empty<SimilarMediaItem>());

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(SimilarMediaLoadError.None, result.Error);
        Assert.AreEqual(0, result.Items.Count);
    }

    [TestMethod]
    public void Failure_UsesAnEmptyStableItemList()
    {
        var result = SimilarMediaLoadResult.Failure(SimilarMediaLoadError.Forbidden);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(SimilarMediaLoadError.Forbidden, result.Error);
        Assert.AreEqual(0, result.Items.Count);
    }
}
