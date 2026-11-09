# Windows Core Audio API - Exact COM Interface Definitions

## IMMDeviceEnumerator (mmdevapi.h)
CLSID: {BCDE0395-E52F-467C-8E3D-C4579291692E}
IID: {A95664D2-9614-4F35-A746-DE8DB63617E6}

Methods:
- EnumAudioEndpoints(EDataFlow dataFlow, DWORD dwStateMask, IMMDeviceCollection** ppDevices)
- GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, IMMDevice** ppEndpoint)
- GetDevice(LPCWSTR pwstrId, IMMDevice** ppDevice)
- RegisterEndpointNotificationCallback(IMMNotificationClient* pClient)
- UnregisterEndpointNotificationCallback(IMMNotificationClient* pClient)

## IMMDevice (mmdevapi.h)
IID: {D666063F-1587-4E43-81F1-B948E807363F}

Methods:
- Activate(REFIID iid, DWORD dwClsCtx, PROPVARIANT* pActivationParams, void** ppInterface)
- OpenPropertyStore(DWORD stgmAccess, IPropertyStore** ppProperties)
- GetId(LPWSTR* ppstrId)
- GetState(DWORD* pdwState)

## IAudioEndpointVolume (EndpointVolume.h)
IID: {5CDF2C82-841E-4546-9722-0CF74078229A}

Methods:
- RegisterControlChangeNotify(IAudioEndpointVolumeCallback* pNotify)
- UnregisterControlChangeNotify(IAudioEndpointVolumeCallback* pNotify)
- GetChannelCount(UINT* pnChannelCount)
- SetMasterVolumeLevel(float fLevelDB, LPCGUID pguidEventContext)
- SetMasterVolumeLevelScalar(float fLevel, LPCGUID pguidEventContext)
- GetMasterVolumeLevel(float* pfLevelDB)
- GetMasterVolumeLevelScalar(float* pfLevel)
- SetChannelVolumeLevel(UINT nChannel, float fLevelDB, LPCGUID pguidEventContext)
- SetChannelVolumeLevelScalar(UINT nChannel, float fLevel, LPCGUID pguidEventContext)
- GetChannelVolumeLevel(UINT nChannel, float* pfLevelDB)
- GetChannelVolumeLevelScalar(UINT nChannel, float* pfLevel)
- SetMute(BOOL bMute, LPCGUID pguidEventContext)
- GetMute(BOOL* pbMute)
- GetVolumeStepInfo(UINT* pnStep, UINT* pnStepCount)
- VolumeStepUp(LPCGUID pguidEventContext)
- VolumeStepDown(LPCGUID pguidEventContext)
- QueryHardwareSupport(DWORD* pdwHardwareSupportMask)
- GetVolumeRange(float* pflVolumeMindB, float* pflVolumeMaxdB, float* pflVolumeIncrementdB)

## IAudioSessionManager2 (AudioSessionManager.h)
IID: {77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F}

Methods:
- GetSessionEnumerator(IAudioSessionEnumerator** SessionEnumerator)
- RegisterSessionNotification(IAudioSessionNotification* SessionNotification)
- UnregisterSessionNotification(IAudioSessionNotification* SessionNotification)
- RegisterDuckNotification(IAudioSessionNotification* SessionNotification)
- UnregisterDuckNotification(IAudioSessionNotification* SessionNotification)

## IAudioSessionEnumerator (AudioSessionManager.h)
IID: {E2F5BB11-0570-40CA-ACDD-3AA01277DEE8}

Methods:
- GetCount(int* SessionCount)
- GetSession(int SessionCount, IAudioSessionControl** Session)

## IAudioSessionControl (AudioSessionManager.h)
IID: {F4B1A599-7266-4319-9CF1-7C8D6C6D6D5B} (v1)
IID: {BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D} (v2 - IAudioSessionControl2)

Methods (v1):
- GetState(AudioSessionState* pRetVal)
- GetDisplayName(LPWSTR* pRetVal)
- GetIconPath(LPWSTR* pRetVal)
- GetGroupingParam(GUID* pRetVal)
- RegisterAudioSessionNotification(IAudioSessionEvents* NewNotifications)
- UnregisterAudioSessionNotification(IAudioSessionEvents* NewNotifications)

Methods (v2 - additional):
- GetSessionIdentifier(LPWSTR* pRetVal)
- GetSessionInstanceIdentifier(LPWSTR* pRetVal)
- GetProcessId(DWORD* pRetVal)
- IsSystemSoundsSession()
- SetDuckingPreference(BOOL optOut)

## ISimpleAudioVolume (AudioSessionManager.h)
IID: {87CE5498-68D6-44E5-9215-6DA47EF883D8}

Methods:
- SetMasterVolume(float fLevel, LPCGUID EventContext)
- GetMasterVolume(float* pfLevel)
- SetMute(BOOL bMute, LPCGUID EventContext)
- GetMute(BOOL* pbMute)

## IAudioEndpointVolumeCallback (EndpointVolume.h)
IID: {657804FA-D6AD-4496-8A60-352752AF4F89}

Methods:
- OnNotify(PAUDIO_VOLUME_NOTIFICATION_DATA pNotify)

AUDIO_VOLUME_NOTIFICATION_DATA struct:
- GUID guidEventContext
- BOOL bMuted
- float fMasterVolume
- UINT nChannels
- float afChannelVolumes[1]  // Variable length array

## IAudioSessionNotification (AudioSessionManager.h)
IID: {641DD20B-4D41-49CC-ABA3-174B9477BB08}

Methods:
- OnSessionCreated(IAudioSessionControl* NewSession)

## Key Enums

### EDataFlow
- eRender = 0 (speakers, headphones)
- eCapture = 1 (microphone)
- eAll = 2

### ERole
- eConsole = 0 (default)
- eMultimedia = 1
- eCommunications = 2

### AudioSessionState
- AudioSessionStateInactive = 0
- AudioSessionStateActive = 1
- AudioSessionStateExpired = 2
