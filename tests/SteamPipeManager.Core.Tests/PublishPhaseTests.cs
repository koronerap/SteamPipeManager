using SteamPipeManager.Core.Publishing;

namespace SteamPipeManager.Core.Tests;

/// <summary>
/// Aşamanın geri düşmemesi ölçülmüş bir hatanın karşılığı: ilerleme satırları hem tarama
/// hem yükleme sırasında geldiği için başlık "Uploading content"ten sonra tekrar
/// "İçerik taranıyor"a düşüyordu. BuildPatchTool'da da aynı risk var — chunk tarayıcı
/// satırları yükleme başladıktan sonra da gelebiliyor.
/// </summary>
public sealed class PublishPhaseTests
{
    [Theory]
    [InlineData(PublishPhase.Idle, PublishPhase.LoggingIn, PublishPhase.LoggingIn)]
    [InlineData(PublishPhase.LoggingIn, PublishPhase.Preparing, PublishPhase.Preparing)]
    [InlineData(PublishPhase.Preparing, PublishPhase.Scanning, PublishPhase.Scanning)]
    [InlineData(PublishPhase.Scanning, PublishPhase.Uploading, PublishPhase.Uploading)]
    public void The_phase_moves_forward(PublishPhase current, PublishPhase candidate, PublishPhase expected) =>
        Assert.Equal(expected, PublishPhases.Advance(current, candidate));

    [Theory]
    [InlineData(PublishPhase.Uploading, PublishPhase.Scanning)]
    [InlineData(PublishPhase.Uploading, PublishPhase.Preparing)]
    [InlineData(PublishPhase.Scanning, PublishPhase.LoggingIn)]
    [InlineData(PublishPhase.Preparing, PublishPhase.Idle)]
    public void The_phase_never_goes_back(PublishPhase current, PublishPhase candidate) =>
        Assert.Equal(current, PublishPhases.Advance(current, candidate));

    [Fact]
    public void A_finished_build_is_not_dragged_back_by_late_progress_lines()
    {
        // Süreç kapanırken log'un son satırları hâlâ okunuyor olabilir.
        Assert.Equal(
            PublishPhase.Succeeded,
            PublishPhases.Advance(PublishPhase.Succeeded, PublishPhase.Uploading));
    }

    [Fact]
    public void Repeating_the_same_phase_keeps_it()
    {
        Assert.Equal(
            PublishPhase.Scanning,
            PublishPhases.Advance(PublishPhase.Scanning, PublishPhase.Scanning));
    }

    [Theory]
    [InlineData(PublishPhase.Succeeded, true)]
    [InlineData(PublishPhase.Failed, true)]
    [InlineData(PublishPhase.Uploading, false)]
    [InlineData(PublishPhase.Idle, false)]
    public void Finished_phases_are_recognised(PublishPhase phase, bool expected) =>
        Assert.Equal(expected, phase.IsFinished());
}
