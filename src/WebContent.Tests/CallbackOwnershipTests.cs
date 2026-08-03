using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using WebContent.Client;

namespace WebContent.Tests
{
    /// <summary>
    /// Tests for the generation-based ownership guard used to bound delayed and
    /// background callbacks to their owning lifecycle (ticket #15). A token captured
    /// before <see cref="CallbackOwnership.End"/> is ignored afterwards, so callbacks
    /// scheduled by an object that has since been closed are dropped.
    /// </summary>
    public class CallbackOwnershipTests
    {
        [Fact]
        public void Captured_Token_Is_Current_Until_End_Is_Called()
        {
            var ownership = new CallbackOwnership();

            var token = ownership.Capture();

            Assert.True(ownership.IsCurrent(token));
        }

        [Fact]
        public void End_Invalidates_Tokens_Captured_Before_It()
        {
            var ownership = new CallbackOwnership();
            var token = ownership.Capture();

            ownership.End();

            Assert.False(ownership.IsCurrent(token));
        }

        [Fact]
        public void Token_Captured_After_End_Is_Current()
        {
            var ownership = new CallbackOwnership();
            ownership.End();

            var token = ownership.Capture();

            Assert.True(ownership.IsCurrent(token));
        }

        [Fact]
        public void Multiple_Ends_Invalidate_Every_Token_Captured_So_Far()
        {
            var ownership = new CallbackOwnership();
            var first = ownership.Capture();

            ownership.End();
            var second = ownership.Capture();
            Assert.True(ownership.IsCurrent(second));

            ownership.End();
            var third = ownership.Capture();

            Assert.False(ownership.IsCurrent(first));
            Assert.False(ownership.IsCurrent(second));
            Assert.True(ownership.IsCurrent(third));
        }

        [Fact]
        public void Distinct_Ownerships_Do_Not_Affect_Each_Other()
        {
            var first = new CallbackOwnership();
            var second = new CallbackOwnership();

            var firstToken = first.Capture();
            var secondToken = second.Capture();

            first.End();

            Assert.False(first.IsCurrent(firstToken));
            Assert.True(second.IsCurrent(secondToken));
        }

        [Fact]
        public async Task Delayed_Callback_Captured_Before_End_Is_Ignored_After_End()
        {
            var ownership = new CallbackOwnership();
            var ranToCompletion = false;

            var token = ownership.Capture();
            var pending = Task.Delay(TimeSpan.FromMilliseconds(20)).ContinueWith(_ =>
            {
                if (ownership.IsCurrent(token))
                {
                    ranToCompletion = true;
                }
            });

            await Task.Delay(TimeSpan.FromMilliseconds(5));
            ownership.End();
            await pending;

            Assert.False(ranToCompletion);
        }

        [Fact]
        public async Task Delayed_Callback_Captured_After_End_Runs()
        {
            var ownership = new CallbackOwnership();
            var ranToCompletion = false;
            ownership.End();

            var token = ownership.Capture();
            await Task.Delay(TimeSpan.FromMilliseconds(20)).ContinueWith(_ =>
            {
                if (ownership.IsCurrent(token))
                {
                    ranToCompletion = true;
                }
            });

            Assert.True(ranToCompletion);
        }
    }
}
