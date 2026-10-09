/** `2026-10-08 14:05` in the browser's time zone; a dash when there is no value. */
export function formatDateTime(value: string | null | undefined): string {
  if (!value) return '—';
  const t = new Date(value);
  if (Number.isNaN(t.getTime())) return '—';
  const two = (n: number) => String(n).padStart(2, '0');
  return `${t.getFullYear()}-${two(t.getMonth() + 1)}-${two(t.getDate())} ${two(t.getHours())}:${two(t.getMinutes())}`;
}
