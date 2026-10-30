module VideoDlssTests

// The Video tab hands ffmpeg and the engine numbers it works out itself: the
// processed size, each picture's time, the encoder. A wrong one is a video that
// will not encode, plays at the wrong speed or is squashed, so they are pinned here.

open Xunit
open DLSS_5_MANAGER.Services.VideoDlss

let private probeOf w h num den =
    { Width = w; Height = h; RateNum = num; RateDen = den; Duration = 10.0; HasAudio = true; Title = "clip" }

let private job codec nvenc =
    { Source = "clip.mp4"
      Output = "out.mp4"
      Container = Mp4
      Codec = codec
      Quality = Balanced
      FrameGen = 2
      MaxHeight = None
      UseNvenc = nvenc
      KeepDownload = false }

[<Fact>]
let ``a video taller than the cap is scaled down keeping its shape, in even sizes`` () =
    Assert.Equal((1920, 1080), processedSize (probeOf 3840 2160 30L 1L) (Some 1080))
    Assert.Equal((1280, 720), processedSize (probeOf 1920 1080 30L 1L) (Some 720))
    // 853.33 rounds to 853, kept even for the encoders.
    Assert.Equal((852, 480), processedSize (probeOf 1280 720 30L 1L) (Some 480))

[<Fact>]
let ``a video within the cap, or with none, keeps its size, made even`` () =
    Assert.Equal((1280, 720), processedSize (probeOf 1280 720 30L 1L) (Some 1080))
    Assert.Equal((1920, 1080), processedSize (probeOf 1920 1080 30L 1L) None)
    Assert.Equal((640, 360), processedSize (probeOf 641 361 30L 1L) None)

[<Fact>]
let ``each picture's time follows the frame rate, NTSC rates included`` () =
    Assert.Equal(33333L, periodMicroseconds (probeOf 1 1 30L 1L))
    Assert.Equal(33367L, periodMicroseconds (probeOf 1 1 30000L 1001L))
    Assert.Equal(16667L, periodMicroseconds (probeOf 1 1 60L 1L))
    Assert.Equal(41708L, periodMicroseconds (probeOf 1 1 24000L 1001L))

[<Fact>]
let ``every container offers codecs it can hold`` () =
    Assert.Equal<Codec list>([ VP9; AV1 ], codecsFor WebM)
    Assert.DoesNotContain(VP9, codecsFor Mp4)
    Assert.Contains(ProRes, codecsFor Mov)
    Assert.Equal(".webm", extensionOf WebM)

[<Fact>]
let ``the NVIDIA encoder is used only for the codecs it has`` () =
    let encoder (args: string list) = args |> List.skipWhile ((<>) "-c:v") |> List.item 1
    Assert.Equal("h264_nvenc", encoder (encoderArgs (job H264 true)))
    Assert.Equal("libx264", encoder (encoderArgs (job H264 false)))
    Assert.Equal("hevc_nvenc", encoder (encoderArgs (job H265 true)))
    Assert.Equal("libvpx-vp9", encoder (encoderArgs (job VP9 true)))
    Assert.Equal("prores_ks", encoder (encoderArgs (job ProRes true)))

[<Fact>]
let ``WebM sound is Opus and the others' AAC`` () =
    Assert.Equal("libopus", audioArgs WebM |> List.item 1)
    Assert.Equal("aac", audioArgs Mp4 |> List.item 1)

[<Fact>]
let ``a result is named after its source, beside the user's videos, in the chosen container`` () =
    let path = defaultOutput @"C:\clips\holiday.mp4" Mkv
    Assert.EndsWith(@"holiday (DLSS 5).mkv", path)
    Assert.True(looksLikeUrl "https://www.youtube.com/watch?v=abc")
    Assert.False(looksLikeUrl @"C:\clips\holiday.mp4")
