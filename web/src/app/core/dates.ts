/** Date/time helpers. The API stores UTC; the UI shows and edits times in the moratorium's own time zone. */

export const COMMON_TIME_ZONES = [
  'America/New_York', 'America/Chicago', 'America/Denver', 'America/Phoenix',
  'America/Los_Angeles', 'America/Anchorage', 'Pacific/Honolulu', 'America/Puerto_Rico', 'UTC',
];

export function systemTimeZone(): string {
  try { return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC'; } catch { return 'UTC'; }
}

export function timeZoneOptions(extra?: string | null): string[] {
  const set = new Set([systemTimeZone(), ...COMMON_TIME_ZONES]);
  if (extra) set.add(extra);
  return [...set];
}

export function formatDateTime(iso: string | null | undefined, timeZone?: string | null): string {
  if (!iso) return '—';
  try {
    return new Intl.DateTimeFormat('en-US', {
      year: 'numeric', month: 'short', day: '2-digit', hour: 'numeric', minute: '2-digit',
      timeZone: timeZone || undefined, timeZoneName: 'short',
    }).format(new Date(iso));
  } catch {
    return new Date(iso).toLocaleString();
  }
}

/** UTC instant -> "YYYY-MM-DDTHH:mm" wall-clock string in the given zone, for datetime-local inputs. */
export function toLocalInput(iso: string | null | undefined, timeZone: string): string {
  if (!iso) return '';
  const parts = new Intl.DateTimeFormat('en-CA', {
    timeZone, year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit',
    hourCycle: 'h23',
  }).formatToParts(new Date(iso));
  const p = (t: string) => parts.find(x => x.type === t)!.value;
  return `${p('year')}-${p('month')}-${p('day')}T${p('hour')}:${p('minute')}`;
}
