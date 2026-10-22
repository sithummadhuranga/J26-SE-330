/// What the capture check says to the nurse, by reason code. Plain words: what happened, then what to do. Codes live in the logic;
/// these sentences are the only place a translation would have to touch.
const _messages = {
  // The sticker could not be found or used: the photo has to be taken again.
  'no_candidate': 'No sticker found. Put the sticker flat beside the wound.',
  'cut_by_edge': 'The sticker is cut off. Move back so all four corners show.',
  'weak_frame_contrast': 'Glare or shadow on the sticker. Tilt the phone away from the light.',
  'weak_orientation': 'The sticker corner is not clear. Use a clean, undamaged sticker.',
  'layout_mismatch': 'Sticker not recognised. Use the sticker from the kit.',
  'mirrored': 'The sticker looks mirrored. Use the rear camera.',
  'crop_invalid': 'The sticker could not be read. Take the photo again.',
  // The area still works, but the colours may not.
  'grey_clipped': 'Too bright or too dark. Colour may be off.',
  'grey_not_uniform': 'Uneven light on the sticker. Colour may be off.',
  'grey_not_neutral': 'Strong coloured light. Colour may be off.',
  'grey_gain_out_of_range': 'The light is too extreme for colour correction.',
  'skin_patch_off': 'A sticker patch looks off. Colour may be off.',
  'too_few_skin_pixels': 'Skin tone unclear. Include more skin in the photo.',
};

/// The sentence for [code]; the "all good" sentence for null; a safe one for a code this version does not know.
String captureMessage(String? code) {
  if (code == null) return 'Sticker found. The photo looks usable.';
  return _messages[code] ?? "This photo can't be used. Retake it.";
}
