using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using UnityEngine;
using UnityEngine.UI;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Cysharp.Threading.Tasks;
using ExampleRooms;
using LiveKit;
using LiveKit.Internal;
using LiveKit.Internal.FFIClients.Requests;
using LiveKit.Proto;
using LiveKit.Rooms;
using LiveKit.Rooms.Participants;
using LiveKit.Rooms.Streaming.Audio;
using RichTypes;
using UnityEngine.SceneManagement;

public class ExampleRoom : MonoBehaviour
{
    private Room m_Room;
    private Apm apm;

    private readonly Dictionary<IAudioStream, LivekitAudioSource> sourcesMap = new();

    public GridLayoutGroup ViewContainer;
    public RawImage ViewPrefab;
    public Button DisconnectButton;

    [Header("Debug")]
    [SerializeField] private GameObject microphoneObject;

    private void Start()
    {
        StartAsync().Forget();
    }

    private void Update()
    {
        foreach (var remoteParticipantIdentity in m_Room.Participants.RemoteParticipantIdentities())
        {
            var participant = m_Room.Participants.RemoteParticipant(remoteParticipantIdentity)!;
            foreach (var (key, value) in participant.Tracks)
            {
                var track = m_Room.AudioStreams.ActiveStream(remoteParticipantIdentity, key!);
                if (track != null)
                {
                    if (track.TryGetTarget(out var audioStream))
                    {
                        if (sourcesMap.ContainsKey(audioStream) == false)
                        {
                            var livekitAudioSource = LivekitAudioSource.New(true);
                            livekitAudioSource.Construct(track);
                            livekitAudioSource.Play();
                            Debug.Log($"Participant {remoteParticipantIdentity} added track {key}");
                            sourcesMap[audioStream] = livekitAudioSource;
                        }
                    }
                }
            }
        }
    }

    private async UniTaskVoid StartAsync()
    {
        // New Room must be called when WebGL assembly is loaded
        m_Room = new Room();

        // Setup the callbacks before connecting to the Room
        m_Room.Participants.UpdatesFromParticipant += (p, update) =>
        {
            if (update == UpdateFromParticipant.Connected)
                Debug.Log($"Participant connected: {p.Sid}");
        };

        var c = await m_Room.ConnectAsync(JoinMenu.LivekitURL, JoinMenu.RoomToken, CancellationToken.None, true);

        if (c.success == false)
        {
            Debug.Log($"Failed to connect to the room !: {c.errorMessage}");
            return;
        }

        Debug.Log("Connected to the room");

        DisconnectButton.onClick.AddListener(() =>
        {
            m_Room.DisconnectAsync(CancellationToken.None);
            SceneManager.LoadScene("JoinScene", LoadSceneMode.Single);
        });

        apm = Apm.NewDefault();

        microphoneObject = new GameObject("microphone");

        var microphoneName = Microphone.devices!.First();

        var audioSource = microphoneObject.AddComponent<AudioSource>();
        audioSource.loop = true;
        audioSource.clip = Microphone.Start(microphoneName, true, 1, 48000); //frequency is not guaranteed
        // Wait until mic is initialized
        await UniTask.WaitWhile(() => !(Microphone.GetPosition(microphoneName) > 0)).Timeout(TimeSpan.FromSeconds(5));

        var audioFilter = microphoneObject.AddComponent<AudioFilter>();
        // Prevent microphone feedback
        microphoneObject.AddComponent<OmitAudioFilter>();
        // Play back the captured audio
        audioSource.Play();

        // Optimised version won't work for some reason
        //var source = new OptimizedMonoRtcAudioSource(audioFilter);
        //source.Start();
        var source = new MicrophoneRtcAudioSource(audioSource, audioFilter, apm);
        source.Start();

        var myTrack = m_Room.AudioTracks.CreateAudioTrack("own", source);
        var trackOptions = new TrackPublishOptions
        {
            AudioEncoding = new AudioEncoding
            {
                MaxBitrate = 124000
            },
            Source = TrackSource.SourceMicrophone
        };
        var publishTask = m_Room.Participants.LocalParticipant()
            .PublishTrack(myTrack, trackOptions, CancellationToken.None);
        await UniTask.WaitUntil(() => publishTask.IsDone);
        Debug.Log("Init finished");
    }

    private void OnDestroy()
    {
        if (m_Room != null)
        {
            m_Room.DisconnectAsync(CancellationToken.None);
        }

        if (apm != null)
        {
            apm.Dispose();
            apm = null;
        }
    }


}

public class Apm : IDisposable
{
    private readonly FfiHandle apmHandle;

    public Apm(
        bool echoCancellerEnabled,
        bool noiseSuppressionEnabled,
        bool gainControllerEnabled,
        bool highPassFilterEnabled)
    {
        using var apmRequest = FFIBridge.Instance.NewRequest<NewApmRequest>();
        apmRequest.request.EchoCancellerEnabled = echoCancellerEnabled;
        apmRequest.request.NoiseSuppressionEnabled = noiseSuppressionEnabled;
        apmRequest.request.GainControllerEnabled = gainControllerEnabled;
        apmRequest.request.HighPassFilterEnabled = highPassFilterEnabled;

        using var response = apmRequest.Send();
        FfiResponse apmResponse = response;
        apmHandle = IFfiHandleFactory.Default.NewFfiHandle(apmResponse.NewApm.Apm.Handle.Id);
    }

    public static Apm NewDefault()
    {
        return new Apm(true, true, true, true);
    }

    public void Dispose()
    {
        lock (this)
        {
            apmHandle.Dispose();
        }
    }

    /// <summary>
    /// Processes the stream that goes from far end and is played by speaker
    /// </summary>
    public Result ProcessReverseStream(ApmFrame frame)
    {
        lock (this)
        {
            unsafe
            {
                fixed (void* ptr = frame.data)
                {
                    using var apmRequest = FFIBridge.Instance.NewRequest<LiveKit.Proto.ApmProcessReverseStreamRequest>();
                    apmRequest.request.ApmHandle = (ulong)apmHandle.DangerousGetHandle().ToInt64();
                    apmRequest.request.DataPtr = new UIntPtr(ptr).ToUInt64();
                    apmRequest.request.NumChannels = frame.numChannels;
                    apmRequest.request.SampleRate = frame.sampleRate.valueHz;
                    apmRequest.request.Size = frame.SizeInBytes;

                    using var wrap = apmRequest.Send();
                    FfiResponse response = wrap;
                    var streamResponse = response.ApmProcessReverseStream;

                    if (streamResponse.HasError)
                        Result.ErrorResult($"Cannot {nameof(ProcessReverseStream)} due error: {streamResponse.Error}");

                    return Result.SuccessResult();
                }
            }
        }
    }

    /// <summary>
    /// Processes the stream that goes from microphone
    /// </summary>
    public Result ProcessStream(ApmFrame apmFrame)
    {
        lock (this)
        {
            unsafe
            {
                fixed (void* ptr = apmFrame.data)
                {
                    using var apmRequest = FFIBridge.Instance.NewRequest<LiveKit.Proto.ApmProcessStreamRequest>();
                    apmRequest.request.ApmHandle = (ulong)apmHandle.DangerousGetHandle().ToInt64();
                    apmRequest.request.DataPtr = new UIntPtr(ptr).ToUInt64();
                    apmRequest.request.NumChannels = apmFrame.numChannels;
                    apmRequest.request.SampleRate = apmFrame.sampleRate.valueHz;
                    apmRequest.request.Size = apmFrame.SizeInBytes;

                    using var wrap = apmRequest.Send();
                    FfiResponse response = wrap;
                    var streamResponse = response.ApmProcessStream;

                    if (streamResponse.HasError)
                        Result.ErrorResult($"Cannot {nameof(ProcessStream)} due error: {streamResponse.Error}");

                    return Result.SuccessResult();
                }
            }
        }
    }

    public Result SetStreamDelay(int delayMs)
    {
        lock (this)
        {
            using var apmRequest = FFIBridge.Instance.NewRequest<LiveKit.Proto.ApmSetStreamDelayRequest>();
            apmRequest.request.ApmHandle = (ulong)apmHandle.DangerousGetHandle().ToInt64();
            apmRequest.request.DelayMs = delayMs;

            using var wrap = apmRequest.Send();
            FfiResponse response = wrap;
            var delayResponse = response.ApmSetStreamDelay;

            if (delayResponse.HasError)
                return Result.ErrorResult($"Cannot {nameof(SetStreamDelay)} due error: {delayResponse.Error}");

            return Result.SuccessResult();
        }
    }
}

/// <summary>
/// Guarantees frame is 10 ms and is compatible to what WebRTC expects
/// </summary>
public readonly ref struct ApmFrame
{
    public readonly ReadOnlySpan<PCMSample> data;
    public readonly uint numChannels;
    public readonly uint samplesPerChannel;
    public readonly SampleRate sampleRate;

    public uint SizeInBytes => numChannels * samplesPerChannel * PCMSample.BytesPerSample;

    private ApmFrame(ReadOnlySpan<PCMSample> data, uint numChannels, uint samplesPerChannel, SampleRate sampleRate)
    {
        this.data = data;
        this.numChannels = numChannels;
        this.samplesPerChannel = samplesPerChannel;
        this.sampleRate = sampleRate;
    }


    /// <summary>
    ///     Cannot use Result due ref limitations in C#
    /// </summary>
    public static ApmFrame New(
        ReadOnlySpan<PCMSample> data,
        uint numChannels,
        uint samplesPerChannel,
        SampleRate sampleRate,
        out string? error)
    {
        error = null;

        if (numChannels == 0)
        {
            error = "Number of channels cannot be zero.";
            return default;
        }

        // Expected samples per 10 ms per channel
        uint expectedSamplesPerChannel = sampleRate.valueHz / 100;

        if (samplesPerChannel != expectedSamplesPerChannel)
        {
            error =
                $"Frame must be 10 ms long. Expected {expectedSamplesPerChannel} samples per channel, got {samplesPerChannel}.";
            return default;
        }

        if (data.Length != samplesPerChannel * numChannels)
        {
            error =
                $"Data length ({data.Length}) does not match samplesPerChannel ({samplesPerChannel}) * numChannels ({numChannels}).";
            return default;
        }

        return new ApmFrame(data, numChannels, samplesPerChannel, sampleRate);
    }
}

public readonly struct SampleRate
{
    public static readonly SampleRate Hz48000 = new(48000);

    public readonly uint valueHz;

    private SampleRate(uint value)
    {
        valueHz = value;
    }
}

[SuppressMessage("ReSharper", "BuiltInTypeReferenceStyle")]
[StructLayout(LayoutKind.Sequential)]
public readonly struct PCMSample
{
    public const byte BytesPerSample = 2; // Int16 = Int8 * 2

    public readonly Int16 data;

    public PCMSample(Int16 data)
    {
        this.data = data;
    }
}