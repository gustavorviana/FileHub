using System.Threading;
using System.Threading.Tasks;

namespace FileHub.Tests;

public class ILazyLoadContractTests
{
    [Fact]
    public void ILazyLoad_ExtendsIRefreshable()
    {
        Assert.True(typeof(IRefreshable).IsAssignableFrom(typeof(ILazyLoad)));
    }

    [Fact]
    public void IsLoaded_FlipsFromFalseToTrueAfterRefresh()
    {
        var stub = new StubLazy();

        Assert.False(stub.IsLoaded);

        stub.Refresh();

        Assert.True(stub.IsLoaded);
    }

    private sealed class StubLazy : ILazyLoad
    {
        private bool _loaded;
        public bool IsLoaded => _loaded;
        public void Refresh() => _loaded = true;
        public Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            _loaded = true;
            return Task.CompletedTask;
        }
    }
}
