using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using UnityEngine;
using UnityEngine.UI;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Cysharp.Threading.Tasks;
using LiveKit;
using LiveKit.Internal;
using LiveKit.Internal.FFIClients.Requests;
using LiveKit.Proto;
using LiveKit.Rooms;
using LiveKit.Rooms.Participants;
using LiveKit.Rooms.Streaming.Audio;
using UnityEngine.SceneManagement;

public class ExampleRoom : MonoBehaviour
{
    private Room m_Room;
    private Apm apm;
    private Dictionary<IAudioStream, LivekitAudioSource> sourcesMap = new();

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


        microphoneObject = new GameObject("microphone");
        var audioFilter = microphoneObject.AddComponent<AudioFilter>();

        var microphoneName = Microphone.devices!.First();

        var audioSource = microphoneObject.AddComponent<AudioSource>();
        audioSource.loop = true;
        audioSource.clip = Microphone.Start(microphoneName, true, 1, 48000); //frequency is not guaranteed
        // Wait until mic is initialized
        await UniTask.WaitWhile(() => !(Microphone.GetPosition(microphoneName) > 0)).Timeout(TimeSpan.FromSeconds(5));

        // Play back the captured audio
        audioSource.Play();

        var source = new OptimizedMonoRtcAudioSource(audioFilter);
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


    private class Apm : IDisposable
    {
        private readonly FfiHandle apmHandle;

        public Apm()
        {
            using var apmRequest = FFIBridge.Instance.NewRequest<NewApmRequest>();
            apmRequest.request.EchoCancellerEnabled = true;
            apmRequest.request.NoiseSuppressionEnabled = true;
            apmRequest.request.GainControllerEnabled = true;
            apmRequest.request.HighPassFilterEnabled = true;

            using var response = apmRequest.Send();
            FfiResponse apmResponse = response;
            apmHandle = IFfiHandleFactory.Default.NewFfiHandle(apmResponse.NewApm.Apm.Handle.Id);
        }

        public void Dispose()
        {
            apmHandle.Dispose();
        }

        // TODO explicit Result<T> return type
        /// <summary>
        /// Processes the stream that goes from far end and plays by speaker
        /// </summary>
        public void ProcessReverseStream(
            ReadOnlySpan<PCMSample> data,
            uint numChannels,
            uint samplesPerChannel,
            SampleRate sampleRate)
        {
            uint sizeInBytes = numChannels * samplesPerChannel * PCMSample.BytesPerSample;

            unsafe
            {
                fixed (void* ptr = data)
                {
                    using var apmRequest = FFIBridge.Instance.NewRequest<LiveKit.Proto.ApmProcessReverseStreamRequest>();
                    apmRequest.request.ApmHandle = (ulong)apmHandle.DangerousGetHandle().ToInt64();
                    apmRequest.request.DataPtr = new UIntPtr(ptr).ToUInt64();
                    apmRequest.request.NumChannels = numChannels;
                    apmRequest.request.SampleRate = sampleRate.valueHz;
                    apmRequest.request.Size = sizeInBytes;

                    using var wrap = apmRequest.Send();
                    FfiResponse response = wrap;
                    var streamResponse = response.ApmProcessReverseStream;

                    if (streamResponse.HasError)
                        Debug.LogError($"Cannot {nameof(ProcessReverseStream)} due error: {streamResponse.Error}");
                }
            }
        }

        // TODO explicit Result<T> return type
        /// <summary>
        /// Processes the stream that goes from microphone
        /// </summary>
        public void ProcessStream(
            ReadOnlySpan<PCMSample> data,
            uint numChannels,
            uint samplesPerChannel,
            SampleRate sampleRate)
        {
            uint sizeInBytes = numChannels * samplesPerChannel * PCMSample.BytesPerSample;

            unsafe
            {
                fixed (void* ptr = data)
                {
                    using var apmRequest = FFIBridge.Instance.NewRequest<LiveKit.Proto.ApmProcessStreamRequest>();
                    apmRequest.request.ApmHandle = (ulong)apmHandle.DangerousGetHandle().ToInt64();
                    apmRequest.request.DataPtr = new UIntPtr(ptr).ToUInt64();
                    apmRequest.request.NumChannels = numChannels;
                    apmRequest.request.SampleRate = sampleRate.valueHz;
                    apmRequest.request.Size = sizeInBytes;

                    using var wrap = apmRequest.Send();
                    FfiResponse response = wrap;
                    var streamResponse = response.ApmProcessStream;

                    if (streamResponse.HasError)
                        Debug.LogError($"Cannot {nameof(ProcessStream)} due error: {streamResponse.Error}");
                }
            }
        }
    }

    [SuppressMessage("ReSharper", "BuiltInTypeReferenceStyle")]
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PCMSample
    {
        public const byte BytesPerSample = 2; // Int16 = Int8 * 2

        public readonly Int16 data;

        public PCMSample(Int16 data)
        {
            this.data = data;
        }
    }

    private readonly struct SampleRate
    {
        public static readonly SampleRate Hz48000 = new(48000);

        public readonly uint valueHz;

        public SampleRate(uint value)
        {
            valueHz = value;
        }
    }
}