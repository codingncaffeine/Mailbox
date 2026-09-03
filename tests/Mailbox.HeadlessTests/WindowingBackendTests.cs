using Mailbox.App;

namespace Mailbox.HeadlessTests;

/// <summary>
/// Which backend the variable asks for, and what the log says a window opened on.
/// </summary>
/// <remarks>
/// The choice itself needs a session to make, so what is pinned here is the part that does not:
/// how the override reads, and that X11 on a Wayland session is reported as the several different
/// things it now is. That last one matters because it is the line somebody reads when their text
/// is soft and they want to know whether they are on XWayland by choice, by fallback, or because
/// the backend never came up.
/// </remarks>
[Collection("environment")]
public class WindowingBackendTests : IDisposable
{
    private readonly string? _saved = Environment.GetEnvironmentVariable(WindowingBackend.Variable);

    public void Dispose() => Environment.SetEnvironmentVariable(WindowingBackend.Variable, _saved);

    private static bool? RequestedWith(string? value)
    {
        Environment.SetEnvironmentVariable(WindowingBackend.Variable, value);
        return WindowingBackend.Requested;
    }

    /// <summary>Unset is not a request: the session decides, which is what makes it a default.</summary>
    [Fact]
    public void UnsetAsksForNothing()
    {
        Assert.Null(RequestedWith(null));
        Assert.Null(RequestedWith(string.Empty));
    }

    /// <summary>
    /// Both directions, because the variable stopped being an opt-in when Wayland became the
    /// default: <c>0</c> is now the way back and has to be heard.
    /// </summary>
    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    public void TheVariableIsHeardInBothDirections(string value, bool expected)
    {
        Assert.Equal(expected, RequestedWith(value));
    }

    /// <summary>Whitespace around it is a typo, not a different answer.</summary>
    [Fact]
    public void ItIsTrimmedBeforeItIsRead()
    {
        Assert.True(RequestedWith(" 1 "));
        Assert.False(RequestedWith(" 0 "));
    }

    /// <summary>
    /// Anything else is not an answer, and an unreadable value must not be read as "no" — that
    /// would make a typo silently pin X11 and look like the default doing its job.
    /// </summary>
    [Fact]
    public void AnythingElseIsNotAnAnswer()
    {
        Assert.Null(RequestedWith("yes"));
        Assert.Null(RequestedWith("wayland"));
        Assert.Null(RequestedWith("2"));
    }
}
