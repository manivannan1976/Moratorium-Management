import { Component, OnInit, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ApiService, toApiError } from '../../core/api.service';
import { formatDateTime } from '../../core/dates';
import { AuditEntry } from '../../core/models';

const PAGE = 50;

@Component({
  selector: 'app-audit',
  imports: [RouterLink],
  template: `
    <div class="page-head">
      <div>
        <h1>Audit History</h1>
        <div class="sub">
          @if (id()) { Changes to {{ id() }}. } @else { Changes across all moratoriums. }
          Entries are read-only and cannot be modified.
        </div>
      </div>
      <div class="actions">
        @if (id()) { <a class="btn btn-secondary" [routerLink]="['/moratoriums', id()]">Back to Details</a> }
        @else { <a class="btn btn-secondary" routerLink="/moratoriums">Back to Dashboard</a> }
      </div>
    </div>

    @if (error()) { <div class="banner banner-error" role="alert">{{ error() }}</div> }

    <section class="card">
      @if (!loading() && items().length === 0 && !error()) {
        <div class="empty">No audit entries found.</div>
      } @else {
        <div class="table-wrap">
          <table class="grid">
            <thead>
              <tr>
                <th>Date/Time</th>
                @if (!id()) { <th>Moratorium</th> }
                <th>User</th><th>Action</th><th>Previous Value</th><th>New Value</th>
              </tr>
            </thead>
            <tbody>
              @for (a of items(); track a.id) {
                <tr>
                  <td style="white-space: nowrap">{{ fmt(a.timestamp) }}</td>
                  @if (!id()) {
                    <td><a [routerLink]="['/moratoriums', a.moratoriumId]">{{ a.moratoriumName }}</a><div class="muted">{{ a.moratoriumId }}</div></td>
                  }
                  <td>{{ a.user }}</td>
                  <td><strong>{{ a.action }}</strong>@if (a.detail) { <div class="muted">{{ a.detail }}</div> }</td>
                  <td style="overflow-wrap: anywhere; max-width: 320px">{{ a.previousValue || '—' }}</td>
                  <td style="overflow-wrap: anywhere; max-width: 320px">{{ a.newValue || '—' }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
        @if (items().length < total()) {
          <div class="card-body" style="text-align: center; padding: 10px">
            <button class="btn btn-secondary btn-sm" type="button" [disabled]="loading()" (click)="load(items().length)">
              Show More ({{ total() - items().length }} remaining)
            </button>
          </div>
        }
      }
    </section>
  `,
})
export class Audit implements OnInit {
  /** Present on /moratoriums/:id/audit, absent on /audit. */
  readonly id = input<string>();
  private api = inject(ApiService);

  items = signal<AuditEntry[]>([]);
  total = signal(0);
  loading = signal(false);
  error = signal('');

  ngOnInit() { this.load(0); }

  load(skip: number) {
    this.loading.set(true);
    this.api.audit(this.id() ?? null, skip, PAGE).subscribe({
      next: r => {
        this.items.update(cur => (skip === 0 ? r.items : [...cur, ...r.items]));
        this.total.set(r.total);
        this.loading.set(false);
      },
      error: e => { this.error.set(toApiError(e).message); this.loading.set(false); },
    });
  }

  fmt(iso: string) { return formatDateTime(iso); }
}
