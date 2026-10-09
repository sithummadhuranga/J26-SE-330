import { describe, expect, it } from 'vitest';
import { fuzzyMatch } from './search';

const row = ['n.silva', 'Account deactivated', 'Failed failure', 'Wrong password', 'dev-a41c', 'admin.demo'];

describe('fuzzyMatch', () => {
  it('matches what is typed, ignoring case', () => {
    expect(fuzzyMatch('SILVA', row)).toBe(true);
    expect(fuzzyMatch('dev-a41', row)).toBe(true);
    expect(fuzzyMatch('', row)).toBe(true);
  });

  it('tolerates small spelling mistakes', () => {
    expect(fuzzyMatch('failiure', row)).toBe(true);
    expect(fuzzyMatch('faild', row)).toBe(true);
    expect(fuzzyMatch('deactvated', row)).toBe(true);
    expect(fuzzyMatch('pasword', row)).toBe(true);
    expect(fuzzyMatch('wrnog', row)).toBe(true);
    expect(fuzzyMatch('deactvat', row)).toBe(true); // a misspelled start of a word
  });

  it('every word must match, and short words must be exact', () => {
    expect(fuzzyMatch('silva wrong', row)).toBe(true);
    expect(fuzzyMatch('silva perera', row)).toBe(false);
    expect(fuzzyMatch('xyz', row)).toBe(false);
    expect(fuzzyMatch('unlocked', row)).toBe(false);
  });
});
