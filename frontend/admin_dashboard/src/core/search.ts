/** Splits text into lower-case words. Letters and digits make up words, and anything else separates them. */
export function words(text: string): string[] {
  return text.toLowerCase().split(/[^a-z0-9]+/).filter(Boolean);
}

/**
 * Counts spelling differences, where a missing, extra, wrong or swapped letter counts as one each.
 * It stops counting once it goes past [max].
 */
function distance(a: string, b: string, max: number): number {
  if (Math.abs(a.length - b.length) > max) return max + 1;
  let before: number[] = [];
  let prev = Array.from({ length: b.length + 1 }, (_, j) => j);
  for (let i = 1; i <= a.length; i++) {
    const row = [i];
    let best = i;
    for (let j = 1; j <= b.length; j++) {
      const cost = a[i - 1] === b[j - 1] ? 0 : 1;
      let value = Math.min(prev[j]! + 1, row[j - 1]! + 1, prev[j - 1]! + cost);
      if (i > 1 && j > 1 && a[i - 1] === b[j - 2] && a[i - 2] === b[j - 1]) value = Math.min(value, before[j - 2]! + 1);
      row.push(value);
      best = Math.min(best, value);
    }
    if (best > max) return max + 1;
    before = prev;
    prev = row;
  }
  return prev[b.length]!;
}

/** How many spelling mistakes we allow: none for short words, one from 4 letters, and two from 8. */
const allowedTypos = (word: string) => (word.length >= 8 ? 2 : word.length >= 4 ? 1 : 0);

/** True when [term] appears as typed, or matches a word apart from a small spelling mistake. */
function termMatches(term: string, haystack: string, candidates: string[]): boolean {
  if (haystack.includes(term)) return true;
  const max = allowedTypos(term);
  if (max === 0) return false;
  // Compare with whole words and with the start of longer words, so "deactvat" still finds "deactivated".
  return candidates.some((c) => distance(term, c, max) <= max || (c.length > term.length && distance(term, c.slice(0, term.length), max) <= max));
}

/** Every word of [query] must match one of [fields], allowing small spelling mistakes. */
export function fuzzyMatch(query: string, fields: (string | null | undefined)[]): boolean {
  const terms = words(query);
  if (terms.length === 0) return true;
  const text = fields.filter((f): f is string => !!f).join(' ').toLowerCase();
  const candidates = words(text);
  return terms.every((term) => termMatches(term, text, candidates));
}
