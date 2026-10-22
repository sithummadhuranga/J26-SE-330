/** Formats a time like `2026-10-08 14:05` in the browser's time zone, or shows a dash if there's no value. */
export function formatDateTime(value: string | null | undefined): string {
  if (!value) return '—';
  const t = new Date(value);
  if (Number.isNaN(t.getTime())) return '—';
  const two = (n: number) => String(n).padStart(2, '0');
  return `${t.getFullYear()}-${two(t.getMonth() + 1)}-${two(t.getDate())} ${two(t.getHours())}:${two(t.getMinutes())}`;
}

/** Up to two initials, the same as the app's InitialsAvatar. "N. Silva" gives "NS" and "admin.demo" gives "AD". */
export function initialsOf(name: string): string {
  const parts = name.trim().split(/[\s._-]+/).filter(Boolean);
  const letters = parts.length > 1 ? parts[0]![0]! + parts[parts.length - 1]![0]! : (parts[0] ?? '?').slice(0, 2);
  return letters.toUpperCase();
}

/** The same greeting as the app's home screen. */
export function greeting(now = new Date()): string {
  const hour = now.getHours();
  return hour < 12 ? 'Good morning' : hour < 17 ? 'Good afternoon' : 'Good evening';
}
