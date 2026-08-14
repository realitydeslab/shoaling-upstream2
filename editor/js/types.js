/**
 * The journey document, typed.
 *
 * This is the keystone of the editor: the same shape is validated by
 * `service/src/journey-schema.mjs` and deserialised by
 * `app/Assets/ShoalingUpstream/Runtime/Journey/JourneyModel.cs`. Three languages describing one
 * file, and the file is the artwork. When this disagrees with either of the others, the failure
 * is silent and turns up as a beat that never fires in a creek.
 *
 * So: derive from the validator, not from memory, and change all three together.
 */
export const LAYERS = ['far', 'mid', 'intimate'];
//# sourceMappingURL=types.js.map