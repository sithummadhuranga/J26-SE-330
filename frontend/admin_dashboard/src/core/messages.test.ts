import { describe, expect, it } from 'vitest';
import { ApiError, TransportError } from './api';
import { codes, describeError } from './messages';
import { NeedsSignIn } from './session';

describe('describeError', () => {
  it('no message shows a status number or a raw reason code', () => {
    const errors = [
      ...[400, 401, 403, 404, 409, 413, 418, 429, 500, 502, 503].map((s) => new ApiError(s)),
      new ApiError(400, 'SOMETHING_NEW'),
      new TransportError('down'),
      new NeedsSignIn('NOT_SIGNED_IN'),
      new Error('boom'),
    ];
    for (const error of errors) {
      const text = describeError(error);
      expect(text, String(error)).not.toMatch(/\d{3}/);
      expect(text, String(error)).not.toMatch(/[A-Z]{2,}_[A-Z]/);
    }
  });

  it('known codes get their own sentence', () => {
    for (const [reason, sentence] of Object.entries(codes)) {
      expect(describeError(new ApiError(401, reason))).toBe(sentence);
    }
    expect(describeError(new ApiError(409, 'CANNOT_DEACTIVATE_SELF'))).toContain('another admin');
  });
});
