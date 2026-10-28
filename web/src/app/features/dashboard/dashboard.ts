import { Component, OnInit, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ApiService, toApiError } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { formatDateTime } from '../../core/dates';
import { MoratoriumSummary } from '../../core/models';

type SectionKey = 'Active' | 'Scheduled' | 'Inactive' | 'Draft';

interface Section {
  key: SectionKey;
  title: string;
  empty: string;
  open: boolean;
  items: MoratoriumSummary[];
  total: number;
  loading: boolean;
}

const PAGE = 10;

@Component({
  selector: 'app-dashboard',
  imports: [RouterLink],
  template: `
    <div class="page-head">
      <div>
        <h1>Moratorium Dashboard</h1>
        <div class="sub">Property moratoriums by status. Evaluation results apply to Active moratoriums only.</div>
      </div>
      <div class="actions">
        @if (auth.isAdmin()) { <a class="btn" routerLink="/moratoriums/new">Create Moratorium</a> }
      </div>
    </div>

    @if (error()) { <div class="banner banner-error" role="alert">{{ error() }}</div> }

    @for (s of visibleSections(); track s.key) {
      <section class="card">
        <div class="card-head">
          <h2>
            <button type="button" class="btn-link" style="text-decoration: none; color: inherit; font-weight: 600; font-size: 16px"
                    [attr.aria-expanded]="s.open" (click)="s.open = !s.open">
              {{ s.open ? '▾' : '▸' }} {{ s.title }} <span class="chip chip-{{ s.key }}">{{ s.total }}</span>
            </button>
          </h2>
        </div>
        @if (s.open) {
          @if (s.loading && s.items.length === 0) {
            <div class="empty">Loading…</div>
          } @else if (s.items.length === 0) {
            <div class="empty">{{ s.empty }}</div>
          } @else {
            <div class="table-wrap">
              <table class="grid">
                <thead>
                  <tr>
                    <th>Moratorium Name</th>
                    @switch (s.key) {
                      @case ('Active') { <th>Activation Date</th><th>Last Updated Date</th> }
                      @case ('Scheduled') { <th>Scheduled Activation Date</th><th>Status</th> }
                      @case ('Inactive') { <th>Active Period</th><th>Last Updated Date</th> }
                      @default { <th>Status</th><th>Last Updated Date</th> }
                    }
                    <th class="right">Actions</th>
                  </tr>
                </thead>
                <tbody>
                  @for (m of s.items; track m.moratoriumId) {
                    <tr>
                      <td>
                        <a [routerLink]="['/moratoriums', m.moratoriumId]"><strong>{{ m.name }}</strong></a>
                        <div class="muted">{{ m.moratoriumId }} · {{ summary(m) }}</div>
                      </td>
                      @switch (s.key) {
                        @case ('Active') { <td>{{ fmt(m.activationDateTime, m) }}</td><td>{{ fmt(m.lastUpdatedDate, m) }}</td> }
                        @case ('Scheduled') { <td>{{ fmt(m.activationDateTime, m) }}</td><td><span class="chip chip-Scheduled">Scheduled</span></td> }
                        @case ('Inactive') { <td>{{ fmt(m.activationDateTime, m) }} – {{ m.deactivationDateTime ? fmt(m.deactivationDateTime, m) : 'n/a' }}</td><td>{{ fmt(m.lastUpdatedDate, m) }}</td> }
                        @default { <td><span class="chip chip-Draft">Draft</span></td><td>{{ fmt(m.lastUpdatedDate, m) }}</td> }
                      }
                      <td class="actions-cell">
                        @if (auth.isAdmin()) {
                          <a class="btn btn-secondary btn-sm" [routerLink]="['/moratoriums', m.moratoriumId, 'edit']">Edit</a>
                        }
                        <a class="btn btn-secondary btn-sm" [routerLink]="['/moratoriums', m.moratoriumId]">View Details</a>
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
            @if (s.items.length < s.total) {
              <div class="card-body" style="text-align: center; padding: 10px">
                <button type="button" class="btn btn-secondary btn-sm" [disabled]="s.loading" (click)="more(s)">
                  Show More ({{ s.total - s.items.length }} remaining)
                </button>
              </div>
            }
          }
        }
      </section>
    }
  `,
})
export class Dashboard implements OnInit {
  private api = inject(ApiService);
  protected auth = inject(AuthService);

  error = signal('');
  private sections: Section[] = [
    { key: 'Active', title: 'Active Moratoriums', empty: 'There are currently no active moratoriums.', open: true, items: [], total: 0, loading: false },
    { key: 'Scheduled', title: 'Scheduled Moratoriums', empty: 'There are no scheduled moratoriums.', open: true, items: [], total: 0, loading: false },
    { key: 'Inactive', title: 'Inactive Moratoriums', empty: 'There are no inactive moratoriums.', open: false, items: [], total: 0, loading: false },
    { key: 'Draft', title: 'Drafts', empty: 'There are no drafts.', open: false, items: [], total: 0, loading: false },
  ];

  visibleSections() {
    return this.sections.filter(s => s.key !== 'Draft' || this.auth.isAdmin());
  }

  ngOnInit() {
    for (const s of this.visibleSections()) this.load(s, 0);
  }

  more(s: Section) { this.load(s, s.items.length); }

  private load(s: Section, skip: number) {
    s.loading = true;
    this.api.list(s.key, undefined, skip, PAGE).subscribe({
      next: r => {
        s.items = skip === 0 ? r.items : [...s.items, ...r.items];
        s.total = r.total;
        s.loading = false;
      },
      error: e => { s.loading = false; this.error.set(toApiError(e).message); },
    });
  }

  fmt(iso: string | null, m: MoratoriumSummary) { return formatDateTime(iso, m.timeZoneId); }

  summary(m: MoratoriumSummary) {
    const parts: string[] = [];
    if (m.stateCount) parts.push(`${m.stateCount} statewide`);
    if (m.countyCount) parts.push(`${m.countyCount} ${m.countyCount === 1 ? 'county' : 'counties'}`);
    if (m.zipCount) parts.push(`${m.zipCount} ZIP`);
    return parts.join(' · ') || 'no geography';
  }
}
