import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { ApiService, toApiError } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { formatDateTime } from '../../core/dates';
import { Area, MoratoriumDetail } from '../../core/models';

@Component({
  selector: 'app-detail',
  imports: [RouterLink],
  template: `
    @if (error()) { <div class="banner banner-error" role="alert">{{ error() }}</div> }
    @if (notice()) { <div class="banner banner-ok" role="status">{{ notice() }}</div> }

    @if (m(); as d) {
      <div class="page-head">
        <div>
          <h1>{{ d.name }} <span class="chip chip-{{ d.status }}">{{ d.status }}</span></h1>
          <div class="sub">{{ d.moratoriumId }}</div>
        </div>
        <div class="actions">
          <a class="btn btn-secondary" routerLink="/moratoriums">Back</a>
          <a class="btn btn-secondary" [routerLink]="['/moratoriums', d.moratoriumId, 'audit']">Audit History</a>
          @if (auth.isAdmin()) {
            <a class="btn btn-secondary" [routerLink]="['/moratoriums', d.moratoriumId, 'edit']">Edit</a>
            @if (d.status === 'Active' || d.status === 'Scheduled') {
              <button class="btn btn-danger" type="button" [disabled]="busy()" (click)="deactivate(d)">
                {{ d.status === 'Active' ? 'Deactivate / Lift' : 'Cancel Schedule' }}
              </button>
            } @else {
              <button class="btn" type="button" [disabled]="busy()" (click)="activate(d)">Activate Now</button>
            }
          }
        </div>
      </div>

      <section class="card">
        <div class="card-head"><h2>General Information</h2></div>
        <div class="card-body">
          <dl class="kv" style="margin: 0">
            <div><dt>Status</dt><dd>{{ d.status }}</dd></div>
            <div><dt>Activation Date</dt><dd>{{ fmt(d.activationDateTime) }}</dd></div>
            <div><dt>Deactivation Date</dt><dd>{{ d.deactivationDateTime ? fmt(d.deactivationDateTime) : 'Until lifted' }}</dd></div>
            <div><dt>Last Updated Date</dt><dd>{{ fmt(d.lastUpdatedDate) }}</dd></div>
            <div><dt>Last Updated By</dt><dd>{{ d.lastUpdatedBy }}</dd></div>
            <div><dt>Created</dt><dd>{{ fmt(d.createdDate) }} by {{ d.createdBy }}</dd></div>
          </dl>
          <h3 style="margin-top: 18px">Event Description</h3>
          <p style="margin: 4px 0 0">{{ d.eventDescription }}</p>
        </div>
      </section>

      <section class="card">
        <div class="card-head">
          <h2>Affected Areas
            <span class="muted" style="font-weight: 400">
              — {{ d.stateCount }} statewide, {{ d.countyCount }} counties, {{ d.zipCount }} ZIP codes
            </span>
          </h2>
          <div class="actions">
            <input type="search" placeholder="Search county or ZIP…" aria-label="Search geography" style="width: 220px"
                   [value]="query()" (input)="query.set($any($event.target).value)">
            <button class="btn btn-secondary btn-sm" type="button" (click)="toggleAll(true)">Expand all</button>
            <button class="btn btn-secondary btn-sm" type="button" (click)="toggleAll(false)">Collapse all</button>
          </div>
        </div>
        <div class="card-body">
          @for (a of filtered(); track a.state) {
            <details class="area" [open]="isOpen(a)" (toggle)="onToggle(a, $event)">
              <summary>
                <strong>{{ a.stateName || a.state }} ({{ a.state }})</strong>
                @if (a.statewide) { <span class="chip chip-Active" style="margin-left: 8px">Statewide</span> }
                <span class="muted" style="margin-left: 8px">
                  {{ a.counties?.length || 0 }} counties · {{ a.zipCodes?.length || 0 }} ZIP codes
                </span>
              </summary>
              <div style="padding: 8px 4px 12px 18px">
                @if (a.statewide) { <p class="muted" style="margin: 0 0 8px">All locations in {{ a.stateName || a.state }} are affected.</p> }
                @if (a.counties?.length) {
                  <div class="lbl">Counties</div>
                  <div>@for (c of a.counties ?? []; track c) { <span class="chip chip-geo">{{ c }}</span> }</div>
                }
                @if (a.zipCodes?.length) {
                  <div class="lbl" style="margin-top: 8px">ZIP Codes</div>
                  <div>@for (z of a.zipCodes ?? []; track z) { <span class="chip chip-geo">{{ z }}</span> }</div>
                }
              </div>
            </details>
          } @empty {
            <div class="empty">{{ query() ? 'No geography matches your search.' : 'No geography has been defined.' }}</div>
          }
        </div>
      </section>
    } @else if (!error()) {
      <div class="empty">Loading…</div>
    }
  `,
  styles: `
    .area { border-bottom: 1px solid #edf0f4; padding: 8px 0; }
    .area:last-child { border-bottom: 0; }
    .area summary { cursor: pointer; }
  `,
})
export class Detail implements OnInit {
  readonly id = input.required<string>();
  private api = inject(ApiService);
  private router = inject(Router);
  protected auth = inject(AuthService);

  m = signal<MoratoriumDetail | null>(null);
  error = signal('');
  notice = signal('');
  busy = signal(false);
  query = signal('');
  private forced = signal<boolean | null>(null);
  private overrides = new Map<string, boolean>();

  filtered = computed<Area[]>(() => {
    const q = this.query().trim().toLowerCase();
    const areas = this.m()?.areas ?? [];
    if (!q) return areas;
    return areas
      .map(a => ({
        ...a,
        counties: (a.counties ?? []).filter(c => c.toLowerCase().includes(q)),
        zipCodes: (a.zipCodes ?? []).filter(z => z.includes(q)),
      }))
      .filter(a => a.counties.length || a.zipCodes.length || (a.state ?? '').toLowerCase() === q || (a.stateName ?? '').toLowerCase().includes(q));
  });

  ngOnInit() {
    const nav = this.router.getCurrentNavigation()?.extras.state as { notice?: string } | undefined;
    if (nav?.notice) this.notice.set(nav.notice);
    else if (history.state?.notice) this.notice.set(history.state.notice);
    this.load();
  }

  private load() {
    this.api.get(this.id()).subscribe({
      next: d => this.m.set(d),
      error: e => this.error.set(toApiError(e).message),
    });
  }

  fmt(iso: string | null) { return formatDateTime(iso, this.m()?.timeZoneId); }

  isOpen(a: Area) {
    if (this.query().trim()) return true;
    const key = a.state ?? '';
    if (this.overrides.has(key)) return this.overrides.get(key)!;
    const forced = this.forced();
    return forced ?? (this.m()?.areas.length ?? 0) <= 3;
  }

  onToggle(a: Area, ev: Event) { this.overrides.set(a.state ?? '', (ev.target as HTMLDetailsElement).open); }

  toggleAll(open: boolean) {
    this.overrides.clear();
    this.forced.set(open);
  }

  activate(d: MoratoriumDetail) { this.run(this.api.activate(d.moratoriumId)); }

  deactivate(d: MoratoriumDetail) {
    if (confirm(`Deactivate "${d.name}"? It will stop applying to evaluations immediately.`)) this.run(this.api.deactivate(d.moratoriumId));
  }

  private run(req: ReturnType<ApiService['activate']>) {
    this.busy.set(true);
    this.error.set('');
    req.subscribe({
      next: r => { this.m.set(r.data); this.notice.set(r.message); this.busy.set(false); },
      error: e => {
        const err = toApiError(e);
        this.error.set(err.validationErrors?.map(v => v.message).join(' ') || err.message);
        this.busy.set(false);
      },
    });
  }
}
