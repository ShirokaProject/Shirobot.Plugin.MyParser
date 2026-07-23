using SharpISOBMFF;
using SharpMP4.Builders;
using SharpMP4.Readers;
using SharpMP4.Tracks;

namespace Shirobot.Plugin.MyParser.Services;

internal static class SharpMp4MediaService
{
    private static readonly Action<Mp4Builder, uint, byte[], int, bool, long>? ProcessTimedRawSample =
        CreateTimedRawSampleDelegate();

    public static void Mux(string videoPath, string audioPath, string outputPath)
    {
        using var videoStream = OpenRead(videoPath);
        using var audioStream = OpenRead(audioPath);
        using var outputStream = new BufferedStream(new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read));

        var videoContainer = ReadContainer(videoStream);
        var videoReader = new VideoReader();
        videoReader.Parse(videoContainer);
        var videoTrack = videoReader.GetTracks().FirstOrDefault(track => track.HandlerType == "vide")
                         ?? throw new InvalidDataException("SharpMP4 未在视频输入中找到视频轨道。");
        var videoTrackBox = GetTrackBox(videoContainer, "vide")
                            ?? throw new InvalidDataException("SharpMP4 未在视频输入中找到视频轨道元数据。");
        var videoMetadata = GetTrackMetadata(videoTrackBox, videoTrack);

        var audioContainer = ReadContainer(audioStream);
        var audioReader = new VideoReader();
        audioReader.Parse(audioContainer);
        var audioTrack = audioReader.GetTracks().FirstOrDefault(track => track.HandlerType == "soun")
                         ?? throw new InvalidDataException("SharpMP4 未在音频输入中找到音频轨道。");
        var audioTrackBox = GetTrackBox(audioContainer, "soun")
                            ?? throw new InvalidDataException("SharpMP4 未在音频输入中找到音频轨道元数据。");
        var audioMetadata = GetTrackMetadata(audioTrackBox, audioTrack);

        var builder = new Mp4Builder(new SingleStreamOutput(outputStream));
        var outputVideoTrack = new PassthroughTrack(videoTrack, videoMetadata);
        var outputAudioTrack = new PassthroughTrack(audioTrack, audioMetadata);
        builder.AddTrack(outputVideoTrack);
        builder.AddTrack(outputAudioTrack);

        CopyInterleavedSamples(
            videoReader,
            videoTrack,
            outputVideoTrack,
            audioReader,
            audioTrack,
            outputAudioTrack,
            builder);
        builder.FinalizeMedia();
    }

    public static void Validate(string path)
    {
        using var stream = OpenRead(path);
        var container = ReadContainer(stream);
        var reader = new VideoReader();
        reader.Parse(container);
        if (!reader.GetTracks().Any(track => track.HandlerType == "vide"))
        {
            throw new InvalidDataException("SharpMP4 未在输出文件中找到视频轨道。");
        }
    }

    private static BufferedStream OpenRead(string path)
    {
        return new BufferedStream(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
    }

    private static Container ReadContainer(Stream stream)
    {
        var container = new Container();
        container.Read(new IsoStream(stream));
        return container;
    }

    private static void CopyInterleavedSamples(
        VideoReader videoReader,
        ITrack sourceVideoTrack,
        ITrack outputVideoTrack,
        VideoReader audioReader,
        ITrack sourceAudioTrack,
        ITrack outputAudioTrack,
        Mp4Builder builder)
    {
        var processSample = ProcessTimedRawSample
                            ?? throw new NotSupportedException("当前 SharpMP4 版本不支持保留逐样本时间戳。");
        var videoSample = videoReader.ReadSample(sourceVideoTrack.TrackID);
        var audioSample = audioReader.ReadSample(sourceAudioTrack.TrackID);

        while (videoSample is not null || audioSample is not null)
        {
            if (audioSample is null
                || videoSample is not null
                && CompareDecodeTime(videoSample, outputVideoTrack.Timescale, audioSample, outputAudioTrack.Timescale) <= 0)
            {
                WriteSample(processSample, builder, outputVideoTrack.TrackID, videoSample!);
                videoSample = videoReader.ReadSample(sourceVideoTrack.TrackID);
            }
            else
            {
                WriteSample(processSample, builder, outputAudioTrack.TrackID, audioSample);
                audioSample = audioReader.ReadSample(sourceAudioTrack.TrackID);
            }
        }
    }

    private static int CompareDecodeTime(MediaSample left, uint leftTimescale, MediaSample right, uint rightTimescale)
    {
        return ((decimal)left.DTS / leftTimescale).CompareTo((decimal)right.DTS / rightTimescale);
    }

    private static void WriteSample(
        Action<Mp4Builder, uint, byte[], int, bool, long> processSample,
        Mp4Builder builder,
        uint outputTrackId,
        MediaSample sample)
    {
        processSample(
            builder,
            outputTrackId,
            sample.Data,
            sample.Duration,
            sample.IsRandomAccessPoint,
            sample.PTS - sample.DTS);
    }

    private static Action<Mp4Builder, uint, byte[], int, bool, long>? CreateTimedRawSampleDelegate()
    {
        var method = typeof(Mp4Builder).GetMethod(
            nameof(Mp4Builder.ProcessRawSample),
            [typeof(uint), typeof(byte[]), typeof(int), typeof(bool), typeof(long)]);
        return method is null
            ? null
            : (Action<Mp4Builder, uint, byte[], int, bool, long>)method.CreateDelegate(
                typeof(Action<Mp4Builder, uint, byte[], int, bool, long>));
    }

    private static TrackBox? GetTrackBox(Container container, string handlerType)
    {
        return container.Children
            .OfType<MovieBox>()
            .SelectMany(movie => movie.Children.OfType<TrackBox>())
            .Where(track => IsoStream.ToFourCC(track.Children.OfType<MediaBox>().Single().Children.OfType<HandlerBox>().Single().HandlerType) == handlerType)
            .FirstOrDefault();
    }

    private static TrackMetadata GetTrackMetadata(TrackBox trackBox, ITrack sourceTrack)
    {
        var media = trackBox.Children.OfType<MediaBox>().Single();
        var sampleTable = media.Children.OfType<MediaInformationBox>().Single()
            .Children.OfType<SampleTableBox>().Single();
        var timeToSample = sampleTable.Children.OfType<TimeToSampleBox>().Single();
        var sampleEntry = sampleTable.Children.OfType<SampleDescriptionBox>().Single().Children.Single();
        var timescale = media.Children.OfType<MediaHeaderBox>().Single().Timescale;
        var defaultSampleDuration = timeToSample.SampleDelta.FirstOrDefault(duration => duration > 0);

        return new TrackMetadata(
            sampleEntry,
            timescale > 0 ? timescale : sourceTrack.Timescale,
            defaultSampleDuration > 0 ? checked((int)defaultSampleDuration) : sourceTrack.DefaultSampleDuration);
    }

    private sealed record TrackMetadata(Box SampleEntry, uint Timescale, int DefaultSampleDuration);

    private sealed class PassthroughTrack : TrackBase
    {
        private readonly ITrack sourceTrack;
        private readonly Box sampleEntry;

        public PassthroughTrack(ITrack sourceTrack, TrackMetadata metadata)
        {
            this.sourceTrack = sourceTrack;
            sampleEntry = metadata.SampleEntry;
            Language = sourceTrack.Language;
            Timescale = metadata.Timescale;
            DefaultSampleDuration = metadata.DefaultSampleDuration;
            CompatibleBrand = sourceTrack.CompatibleBrand;
            DefaultSampleFlags = sourceTrack.DefaultSampleFlags;
        }

        public override string HandlerName => sourceTrack.HandlerName;
        public override string HandlerType => sourceTrack.HandlerType;
        public override string Language { get; set; }

        public override Box CreateSampleEntryBox() => sampleEntry;

        public override void FillTkhdBox(TrackHeaderBox tkhd)
        {
            sourceTrack.FillTkhdBox(tkhd);
        }

        public override ITrack Clone() => new PassthroughTrack(
            sourceTrack,
            new TrackMetadata(sampleEntry, Timescale, DefaultSampleDuration));
    }
}
