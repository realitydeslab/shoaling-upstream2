//
//  PhaseBridge.m — Apple PHASE, exposed to Unity as thirteen C functions.
//
//  This file is deliberately dull. Every decision about how the piece sounds is made in C# in
//  Runtime/Audio, and everything here does is hold PHASE objects alive and copy numbers into
//  them. The reason is testability: PHASE does not exist in the Unity editor, so anything
//  implemented on this side of the boundary can only ever be checked by standing at a creek with
//  a device, and a crossfade law that can only be checked at a creek does not get checked.
//
//  Three consequences of that, each of which looks like PHASE being under-used and is not:
//
//  * The spatial mixer's distance rolloff is DISABLED (rolloffFactor = 0). The distance law is
//    computed in DistanceField.cs so that the desk stand-in and the device agree exactly. PHASE
//    only exposes a single rolloff scalar, so there is no setting here that could be made to
//    match the editor's curve.
//  * Culling is off (a 1000 m cull distance against a ~71 m site). Sources are stopped by the
//    engine at 1.35x their reach, where the law has already reached about -53 dB. A hard cutoff
//    at a cull radius is audible; a fade that is already inaudible is not.
//  * The three distance recordings are three sampler nodes with independent gain
//    metaparameters, not a blend node with useAutoDistanceBlend. The blend node would run at
//    audio rate, which is better, but it would also put the law somewhere it cannot be asserted.
//    Frame rate is 16-33 ms against a 50-100 ms budget for continuous field parameters.
//
//  Linking: `@import PHASE;` relies on clang module autolinking, which Unity's generated Xcode
//  project has on by default. If it is ever turned off, PHASE.framework has to be added to the
//  UnityFramework target by hand or by a post-process build step.
//
@import Foundation;
@import AVFoundation;
@import PHASE;

#import <simd/simd.h>

// Must match ShoalVoicing.MaxVoices and ShoalVoicing.VoiceAzimuthsDeg.
static const int kVoiceCount = 8;
static const float kVoiceAzimuthsDeg[kVoiceCount] = {
    -110.0f, -78.57f, -47.14f, -15.71f, 15.71f, 47.14f, 78.57f, 110.0f
};

static NSString *const kLayerGainIds[3] = { @"gain-far", @"gain-mid", @"gain-intimate" };

#pragma mark - state

@interface SUPhaseSource : NSObject {
@public
    // A C array cannot be a property, and the alternative — three named floats — would have to be
    // unpacked by hand at every one of the four places that index by slot.
    float pendingGains[3];
}
@property (nonatomic, strong) PHASESource *source;
@property (nonatomic, strong) PHASESoundEvent *event;
@property (nonatomic, strong) NSMutableArray *paths;   // NSNull where a layer is absent
@property (nonatomic, strong) NSMutableArray *looping;
@property (nonatomic, strong) NSString *eventId;
@property (nonatomic, assign) BOOL built;
@end

@implementation SUPhaseSource
- (instancetype)init {
    if ((self = [super init])) {
        _paths = [@[[NSNull null], [NSNull null], [NSNull null]] mutableCopy];
        _looping = [@[@YES, @YES, @YES] mutableCopy];
    }
    return self;
}
@end

@interface SUPhaseVoice : NSObject
@property (nonatomic, strong) PHASEAmbientMixerDefinition *mixer;
@property (nonatomic, strong) PHASESoundEvent *event;
@property (nonatomic, strong) NSString *eventId;
@end
@implementation SUPhaseVoice
@end

static PHASEEngine *gEngine = nil;
static PHASEListener *gListener = nil;
static PHASESpatialMixerDefinition *gSpatialMixer = nil;
static PHASEChannelMixerDefinition *gHeadMixer = nil;
static PHASEReverbPreset gReverbPreset = PHASEReverbPresetMediumRoom;
static NSMutableArray<SUPhaseSource *> *gSources = nil;
static NSMutableArray<SUPhaseVoice *> *gVoices = nil;
static NSMutableDictionary<NSString *, NSString *> *gAssets = nil;   // file path -> asset identifier
static NSMutableSet<PHASESoundEvent *> *gOneShots = nil;             // kept alive until they finish
static NSString *gBedLowPath = nil;
static NSString *gBedHighPath = nil;
static BOOL gBedsDirty = NO;
static int gNextId = 0;

#pragma mark - helpers

static NSString *SUNextIdentifier(NSString *prefix) {
    return [NSString stringWithFormat:@"%@.%d", prefix, gNextId++];
}

/// Registers a file once and returns its asset identifier, or nil. Registering the same URL twice
/// is an error in PHASE, and every beat shares its ambience clips with at least one neighbour.
static NSString *SUAsset(NSString *path) {
    if (path.length == 0) return nil;
    NSString *existing = gAssets[path];
    if (existing) return existing;

    NSURL *url = [NSURL fileURLWithPath:path];
    NSString *identifier = SUNextIdentifier(@"asset");
    AVAudioChannelLayout *layout =
        [[AVAudioChannelLayout alloc] initWithLayoutTag:kAudioChannelLayoutTag_Mono];

    NSError *error = nil;
    // Resident, not streamed: these are short beds that loop for the whole walk, and a disk read
    // on the trigger path is exactly the jitter the latency budget cannot afford.
    NSString *registered = [gEngine.assetRegistry registerSoundAssetAtURL:url
                                                              identifier:identifier
                                                               assetType:PHASEAssetTypeResident
                                                           channelLayout:layout
                                                       normalizationMode:PHASENormalizationModeNone
                                                                   error:&error];
    if (!registered) {
        NSLog(@"[phase] could not register %@: %@", path, error);
        return nil;
    }
    gAssets[path] = registered;
    return registered;
}

static PHASESamplerNodeDefinition *SUSampler(NSString *assetId,
                                             PHASEMixerDefinition *mixer,
                                             BOOL looping,
                                             NSString *gainId) {
    PHASESamplerNodeDefinition *sampler =
        [[PHASESamplerNodeDefinition alloc] initWithSoundAssetIdentifier:assetId
                                                         mixerDefinition:mixer];
    sampler.playbackMode = looping ? PHASEPlaybackModeLooping : PHASEPlaybackModeOneShot;
    // None, not RelativeSpl: the levels arriving from C# are already the composed mix, and
    // letting PHASE re-interpret them as sound pressure would apply the design twice.
    [sampler setCalibrationMode:PHASECalibrationModeNone level:1.0];
    if (gainId) {
        sampler.gainMetaParameterDefinition =
            [[PHASENumberMetaParameterDefinition alloc] initWithValue:0.0
                                                             minimum:0.0
                                                             maximum:1.0
                                                          identifier:gainId];
    }
    return sampler;
}

static void SUSetMeta(PHASESoundEvent *event, NSString *identifier, float value) {
    PHASENumberMetaParameter *parameter =
        (PHASENumberMetaParameter *)event.metaParameters[identifier];
    if (parameter) parameter.value = value;
}

static simd_float4x4 SUTransform(float px, float py, float pz,
                                 float qx, float qy, float qz, float qw) {
    simd_quatf rotation = simd_quaternion(qx, qy, qz, qw);
    simd_float4x4 matrix = simd_matrix4x4(rotation);
    matrix.columns[3] = simd_make_float4(px, py, pz, 1.0f);
    return matrix;
}

#pragma mark - lifecycle

int SUPhaseIsAvailable(void) {
    if (@available(iOS 15.0, *)) return 1;
    return 0;
}

int SUPhaseStart(float masterGainDb, int headTracking, int reverbPreset) {
    if (gEngine) return 1;
    if (@available(iOS 15.0, *)) {} else return 0;

    switch (reverbPreset) {
        case 0:  gReverbPreset = PHASEReverbPresetNone; break;
        case 1:  gReverbPreset = PHASEReverbPresetSmallRoom; break;
        case 3:  gReverbPreset = PHASEReverbPresetLargeRoom; break;
        case 4:  gReverbPreset = PHASEReverbPresetCathedral; break;
        default: gReverbPreset = PHASEReverbPresetMediumRoom; break;
    }

    gEngine = [[PHASEEngine alloc] initWithUpdateMode:PHASEUpdateModeAutomatic];
    gEngine.defaultReverbPreset = gReverbPreset;
    gEngine.outputSpatializationMode = PHASESpatializationModeAutomatic;
    gEngine.unitsPerMeter = 1.0;
    gEngine.outputLevel = powf(10.0f, masterGainDb / 20.0f);

    gSources = [NSMutableArray array];
    gVoices = [NSMutableArray array];
    gAssets = [NSMutableDictionary dictionary];
    gOneShots = [NSMutableSet set];

    gListener = [[PHASEListener alloc] initWithEngine:gEngine];
    gListener.transform = matrix_identity_float4x4;
    if (headTracking) {
        // Costs nothing to ask for and is the cue that resolves front-back confusion. It is
        // honoured only on hardware that supports it; there is no failure to handle.
        gListener.automaticHeadTrackingFlags = PHASEAutomaticHeadTrackingFlagOrientation;
    }
    NSError *error = nil;
    if (![gEngine.rootObject addChild:gListener error:&error]) {
        NSLog(@"[phase] listener: %@", error);
    }

    PHASESpatialPipelineFlags flags =
        PHASESpatialPipelineFlagDirectPathTransmission | PHASESpatialPipelineFlagLateReverb;
    PHASESpatialPipeline *pipeline = [[PHASESpatialPipeline alloc] initWithFlags:flags];
    pipeline.entries[PHASESpatialCategoryDirectPathTransmission].sendLevel = 1.0;
    pipeline.entries[PHASESpatialCategoryLateReverb].sendLevel = 0.2;
    gSpatialMixer = [[PHASESpatialMixerDefinition alloc] initWithSpatialPipeline:pipeline];

    PHASEDistanceModelFadeOutParameters *fade =
        [[PHASEDistanceModelFadeOutParameters alloc] initWithCullDistance:1000.0];
    PHASEGeometricSpreadingDistanceModelParameters *distance =
        [[PHASEGeometricSpreadingDistanceModelParameters alloc] init];
    distance.fadeOutParameters = fade;
    distance.rolloffFactor = 0.0;   // see the header comment: the law lives in C#
    gSpatialMixer.distanceModelParameters = distance;

    AVAudioChannelLayout *mono =
        [[AVAudioChannelLayout alloc] initWithLayoutTag:kAudioChannelLayoutTag_Mono];
    gHeadMixer = [[PHASEChannelMixerDefinition alloc] initWithChannelLayout:mono];

    for (int i = 0; i < kVoiceCount; i++) {
        SUPhaseVoice *voice = [[SUPhaseVoice alloc] init];
        // The shoal is at distance zero — it IS the visitor — so it goes on ambient mixers,
        // head-relative and externalised, with no distance modelling to model a gap that does
        // not exist. The azimuth is baked here because PHASE fixes it at definition time.
        float radians = kVoiceAzimuthsDeg[i] * (float)M_PI / 180.0f;
        simd_quatf orientation = simd_quaternion(radians, simd_make_float3(0.0f, 1.0f, 0.0f));
        voice.mixer = [[PHASEAmbientMixerDefinition alloc] initWithChannelLayout:mono
                                                                     orientation:orientation];
        [gVoices addObject:voice];
    }

    if (![gEngine startAndReturnError:&error]) {
        NSLog(@"[phase] engine failed to start: %@", error);
        gEngine = nil;
        return 0;
    }
    return 1;
}

void SUPhaseStop(void) {
    if (!gEngine) return;
    for (SUPhaseSource *source in gSources) [source.event stopAndInvalidate];
    for (SUPhaseVoice *voice in gVoices) [voice.event stopAndInvalidate];
    for (PHASESoundEvent *event in gOneShots) [event stopAndInvalidate];
    [gEngine stop];
    gEngine = nil;
    gListener = nil;
    gSpatialMixer = nil;
    gHeadMixer = nil;
    gSources = nil;
    gVoices = nil;
    gAssets = nil;
    gOneShots = nil;
    gBedLowPath = nil;
    gBedHighPath = nil;
}

#pragma mark - listener and sources

void SUPhaseSetListener(float px, float py, float pz,
                        float qx, float qy, float qz, float qw) {
    if (!gListener) return;
    gListener.transform = SUTransform(px, py, pz, qx, qy, qz, qw);
}

int SUPhaseCreateSource(const char *sourceId, float x, float y, float z) {
    if (!gEngine) return -1;
    (void)sourceId;

    SUPhaseSource *entry = [[SUPhaseSource alloc] init];
    entry.source = [[PHASESource alloc] initWithEngine:gEngine];
    entry.source.transform = SUTransform(x, y, z, 0.0f, 0.0f, 0.0f, 1.0f);
    entry.eventId = SUNextIdentifier(@"event");

    NSError *error = nil;
    if (![gEngine.rootObject addChild:entry.source error:&error]) {
        NSLog(@"[phase] source: %@", error);
        return -1;
    }

    [gSources addObject:entry];
    return (int)gSources.count - 1;
}

static SUPhaseSource *SUSourceAt(int handle) {
    if (!gSources || handle < 0 || handle >= (int)gSources.count) return nil;
    return gSources[handle];
}

void SUPhaseSetSourceLayer(int handle, int slot, const char *filePath, int loop) {
    SUPhaseSource *entry = SUSourceAt(handle);
    if (!entry || slot < 0 || slot > 2) return;
    entry.paths[slot] = filePath ? (id)@(filePath) : (id)[NSNull null];
    entry.looping[slot] = @(loop != 0);
}

/// Built on first activation rather than on creation: the asset graph is immutable once
/// registered, so it cannot be assembled until every layer has been declared.
static void SUBuildSource(SUPhaseSource *entry) {
    if (entry.built) return;
    entry.built = YES;

    PHASEContainerNodeDefinition *container = [[PHASEContainerNodeDefinition alloc] init];
    int present = 0;

    for (int slot = 0; slot < 3; slot++) {
        id path = entry.paths[slot];
        if (path == [NSNull null]) continue;
        NSString *assetId = SUAsset((NSString *)path);
        if (!assetId) continue;
        BOOL looping = [(NSNumber *)entry.looping[slot] boolValue];
        [container addSubtree:SUSampler(assetId, gSpatialMixer, looping, kLayerGainIds[slot])];
        present++;
    }
    if (present == 0) return;

    NSError *error = nil;
    if (![gEngine.assetRegistry registerSoundEventAssetWithRootNode:container
                                                        identifier:entry.eventId
                                                             error:&error]) {
        NSLog(@"[phase] sound event asset: %@", error);
        return;
    }

    PHASEMixerParameters *parameters = [[PHASEMixerParameters alloc] init];
    [parameters addSpatialMixerParametersWithIdentifier:gSpatialMixer.identifier
                                                 source:entry.source
                                               listener:gListener];

    entry.event = [[PHASESoundEvent alloc] initWithEngine:gEngine
                                          assetIdentifier:entry.eventId
                                          mixerParameters:parameters
                                                    error:&error];
    if (!entry.event) {
        NSLog(@"[phase] sound event: %@", error);
        return;
    }

    // Push whatever the engine last asked for before the first sample is heard: starting at the
    // gain from four frames ago is a click at exactly the moment a source comes into earshot.
    for (int slot = 0; slot < 3; slot++) {
        SUSetMeta(entry.event, kLayerGainIds[slot], entry->pendingGains[slot]);
    }
}

void SUPhaseSetSourceGains(int handle, float far, float mid, float intimate) {
    SUPhaseSource *entry = SUSourceAt(handle);
    if (!entry) return;
    entry->pendingGains[0] = far;
    entry->pendingGains[1] = mid;
    entry->pendingGains[2] = intimate;
    if (!entry.event) return;
    SUSetMeta(entry.event, kLayerGainIds[0], far);
    SUSetMeta(entry.event, kLayerGainIds[1], mid);
    SUSetMeta(entry.event, kLayerGainIds[2], intimate);
}

void SUPhaseSetSourceActive(int handle, int active) {
    SUPhaseSource *entry = SUSourceAt(handle);
    if (!entry) return;

    if (active) {
        SUBuildSource(entry);
        NSError *error = nil;
        if (entry.event && ![entry.event startAndReturnError:&error]) {
            NSLog(@"[phase] start: %@", error);
        }
    } else if (entry.event) {
        [entry.event stopAndInvalidate];
        entry.event = nil;
        entry.built = NO;
        // The asset stays registered; only the event is torn down, so re-entering earshot costs
        // an event allocation rather than a re-read of the file.
        entry.eventId = SUNextIdentifier(@"event");
    }
}

#pragma mark - one-shots and medium

void SUPhasePlayOneShot(const char *filePath, float gain) {
    if (!gEngine || !filePath) return;
    NSString *assetId = SUAsset(@(filePath));
    if (!assetId) return;

    NSString *eventId = SUNextIdentifier(@"oneshot");
    PHASESamplerNodeDefinition *sampler = SUSampler(assetId, gHeadMixer, NO, @"gain");

    NSError *error = nil;
    if (![gEngine.assetRegistry registerSoundEventAssetWithRootNode:sampler
                                                        identifier:eventId
                                                             error:&error]) {
        NSLog(@"[phase] one-shot asset: %@", error);
        return;
    }

    PHASEMixerParameters *parameters = [[PHASEMixerParameters alloc] init];
    PHASESoundEvent *event = [[PHASESoundEvent alloc] initWithEngine:gEngine
                                                    assetIdentifier:eventId
                                                    mixerParameters:parameters
                                                              error:&error];
    if (!event) {
        NSLog(@"[phase] one-shot event: %@", error);
        return;
    }
    SUSetMeta(event, @"gain", gain);

    // Held in a set until the completion handler fires. A confirmation that stops being retained
    // half a second after it starts is a confirmation that sometimes does not happen.
    [gOneShots addObject:event];
    [event startWithCompletion:^(PHASESoundEventStartHandlerReason reason) {
        (void)reason;
        dispatch_async(dispatch_get_main_queue(), ^{
            [gOneShots removeObject:event];
            [gEngine.assetRegistry unregisterAssetWithIdentifier:eventId completion:nil];
        });
    }];
}

int SUPhaseSupportsLowPass(void) {
    // PHASE's public surface has no filter node and no insert point. The brightness half of the
    // medium cue has to be carried by content instead. Reported rather than faked.
    return 0;
}

void SUPhaseSetReverb(float send) {
    if (!gEngine) return;
    // One global preset, no per-source rooms — so the only thing "the field goes dry" can mean
    // on device is the preset going to None, and it has to be a threshold rather than a fade.
    gEngine.defaultReverbPreset = send < 0.15f ? PHASEReverbPresetNone : gReverbPreset;
}

#pragma mark - shoal

void SUPhaseSetShoalBeds(const char *lowPath, const char *highPath) {
    NSString *low = lowPath ? @(lowPath) : @"";
    NSString *high = highPath ? @(highPath) : @"";
    if ([low isEqualToString:gBedLowPath ?: @""] && [high isEqualToString:gBedHighPath ?: @""]) return;
    gBedLowPath = low.length ? low : nil;
    gBedHighPath = high.length ? high : nil;
    gBedsDirty = YES;
}

static void SURebuildVoices(void) {
    if (!gBedsDirty || !gEngine) return;
    gBedsDirty = NO;

    NSString *lowAsset = SUAsset(gBedLowPath);
    NSString *highAsset = SUAsset(gBedHighPath);
    if (!lowAsset && !highAsset) return;

    for (int i = 0; i < (int)gVoices.count; i++) {
        SUPhaseVoice *voice = gVoices[i];
        if (voice.event) {
            [voice.event stopAndInvalidate];
            voice.event = nil;
        }

        PHASEContainerNodeDefinition *container = [[PHASEContainerNodeDefinition alloc] init];
        if (lowAsset)  [container addSubtree:SUSampler(lowAsset,  voice.mixer, YES, @"gain-low")];
        if (highAsset) [container addSubtree:SUSampler(highAsset, voice.mixer, YES, @"gain-high")];

        voice.eventId = SUNextIdentifier(@"shoal");
        NSError *error = nil;
        if (![gEngine.assetRegistry registerSoundEventAssetWithRootNode:container
                                                            identifier:voice.eventId
                                                                 error:&error]) {
            NSLog(@"[phase] shoal asset: %@", error);
            continue;
        }

        PHASEMixerParameters *parameters = [[PHASEMixerParameters alloc] init];
        [parameters addAmbientMixerParametersWithIdentifier:voice.mixer.identifier
                                                  listener:gListener];
        voice.event = [[PHASESoundEvent alloc] initWithEngine:gEngine
                                              assetIdentifier:voice.eventId
                                              mixerParameters:parameters
                                                        error:&error];
        if (!voice.event) {
            NSLog(@"[phase] shoal event: %@", error);
            continue;
        }
        SUSetMeta(voice.event, @"gain-low", 0.0f);
        SUSetMeta(voice.event, @"gain-high", 0.0f);
        if (![voice.event startAndReturnError:&error]) NSLog(@"[phase] shoal start: %@", error);
    }
}

void SUPhaseSetShoalVoice(int index, float lowGain, float highGain) {
    SURebuildVoices();
    if (index < 0 || index >= (int)gVoices.count) return;

    SUPhaseVoice *voice = gVoices[index];
    if (!voice.event) return;
    // Every voice stays running and is gained to zero when the shoal has thinned past it.
    // Stopping and restarting an event would restart the bed from its first sample, and a grain
    // cloud that restarts in step with a donation sounds like a glitch caused by the donation.
    SUSetMeta(voice.event, @"gain-low", lowGain);
    SUSetMeta(voice.event, @"gain-high", highGain);
}
