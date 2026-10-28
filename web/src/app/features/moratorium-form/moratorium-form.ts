import { Component, OnInit, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { ApiService, toApiError } from '../../core/api.service';
import { systemTimeZone, timeZoneOptions, toLocalInput } from '../../core/dates';
import { ApiError, FieldError, MoratoriumDetail, MoratoriumRequest, StateRef } from '../../core/models';

interface AreaRow {
  state: string;
  statewide: boolean;
  counties: string;
  zips: string;
}

/** Split pasted/bulk text on commas, semicolons and new lines (counties) or whitespace too (ZIP codes). */
function split(text: string, zip: boolean): string[] {
  return text.split(zip ? /[,;\s]+/ : /[,;\r\n]+/).map(s => s.trim()).filter(Boolean);
}

@Component({
  selector: 'app-moratorium-form',
  imports: [FormsModule, RouterLink],
  template: `
    <div class="page-head">
      <div>
        <h1>{{ id() ? 'Update Moratorium' : 'Create Moratorium' }}</h1>
        <div class="sub">
          {{ id() ? 'Changes are saved immediately and take effect in evaluation responses.' : 'Define the schedule and the geography the moratorium applies to.' }}
        </div>
      </div>
    </div>

    @if (error()) {
      <div class="banner banner-error" role="alert">
        <strong>{{ error()!.message }}</strong>
        @if (generalErrors().length) { <ul>@for (v of generalErrors(); track $index) { <li>{{ v.message }}</li> }</ul> }
      </div>
    }

    @if (ready()) {
      <form (ngSubmit)="submit(false)" #f="ngForm" novalidate>
        <section class="card">
          <div class="card-head"><h2>1 · Moratorium Information</h2></div>
          <div class="card-body">
            <label class="field"><span class="lbl req">Moratorium Name</span>
              <input type="text" name="name" [(ngModel)]="name" maxlength="200" [class.invalid]="fieldError('name')" autocomplete="off">
              @if (fieldError('name'); as e) { <div class="error-text">{{ e }}</div> }
              <div class="hint">Must be unique among Active and Scheduled moratoriums.</div></label>
            @if (id()) { <div class="hint">Event description is generated automatically from the name, geography and schedule.</div> }
          </div>
        </section>

        <section class="card">
          <div class="card-head"><h2>2 · Schedule</h2></div>
          <div class="card-body">
            <div class="grid-2">
              <label class="field"><span class="lbl req">Activation Date &amp; Time</span>
                <input type="datetime-local" name="act" [(ngModel)]="activation" [disabled]="immediate"
                       [class.invalid]="fieldError('activationDateTime')">
                @if (fieldError('activationDateTime'); as e) { <div class="error-text">{{ e }}</div> }
                @if (!id()) {
                  <label class="check"><input type="checkbox" name="imm" [(ngModel)]="immediate"> Activate immediately</label>
                }
              </label>
              <label class="field"><span class="lbl req">Timezone</span>
                <select name="tz" [(ngModel)]="timeZoneId">
                  @for (z of zones; track z) { <option [value]="z">{{ z }}</option> }
                </select>
                <div class="hint">Defaults to your system time zone ({{ sys }}).</div></label>
            </div>
            <label class="check"><input type="checkbox" name="sd" [(ngModel)]="scheduleDeactivation"> Schedule deactivation</label>
            @if (scheduleDeactivation) {
              <label class="field" style="max-width: 360px"><span class="lbl req">Deactivation Date &amp; Time</span>
                <input type="datetime-local" name="deact" [(ngModel)]="deactivation" [class.invalid]="fieldError('deactivationDateTime')">
                @if (fieldError('deactivationDateTime'); as e) { <div class="error-text">{{ e }}</div> }
                <div class="hint">Must be later than the activation date and time.</div></label>
            }
          </div>
        </section>

        <section class="card">
          <div class="card-head">
            <h2>3 · Affected Areas</h2>
            <button class="btn btn-secondary btn-sm" type="button" (click)="addRow()">Add State</button>
          </div>
          <div class="card-body">
            @for (r of rows; track r; let i = $index) {
              <div class="area-row">
                <div class="area-row-head">
                  <label><span class="lbl req">State</span>
                    <select [name]="'st' + i" [(ngModel)]="r.state" [class.invalid]="rowError(i, 'state')">
                      <option value="">Select a state…</option>
                      @for (s of states(); track s.code) { <option [value]="s.code">{{ s.name }} ({{ s.code }})</option> }
                    </select></label>
                  <label class="check" style="margin-bottom: 8px"><input type="checkbox" [name]="'sw' + i" [(ngModel)]="r.statewide"> Statewide Restriction</label>
                  <span class="spacer"></span>
                  <button class="btn btn-secondary btn-sm" type="button" (click)="removeRow(i)" [disabled]="rows.length === 1 && !r.state && !r.counties && !r.zips">Remove</button>
                </div>
                @if (rowError(i, 'state'); as e) { <div class="error-text" style="margin: -4px 0 8px">{{ e }}</div> }
                @if (r.statewide) { <div class="hint" style="margin-bottom: 8px">Statewide applies to every location in this state; counties and ZIP codes below are optional.</div> }
                <div class="grid-2">
                  <label class="field"><span class="lbl">Counties</span>
                    <textarea [name]="'co' + i" [(ngModel)]="r.counties" rows="3" placeholder="Miami-Dade, Broward, Hillsborough" [class.invalid]="rowError(i, 'counties')"></textarea>
                    <div class="hint">Comma-separated or one per line; paste is supported.</div>
                    @if (rowError(i, 'counties'); as e) { <div class="error-text">{{ e }}</div> }</label>
                  <label class="field"><span class="lbl">ZIP Codes</span>
                    <textarea [name]="'zp' + i" [(ngModel)]="r.zips" rows="3" placeholder="33101, 33102, 02108" [class.invalid]="rowError(i, 'zipCodes')"></textarea>
                    <div class="hint">Five digits each, comma-separated; leading zeros are kept.</div>
                    @if (rowError(i, 'zipCodes'); as e) { <div class="error-text">{{ e }}</div> }</label>
                </div>
                @if (rowError(i, '')) { <div class="error-text">{{ rowError(i, '') }}</div> }
              </div>
            }
          </div>
        </section>

        <section class="card">
          <div class="card-head"><h2>4 · Business Restrictions</h2></div>
          <div class="card-body">
            <div class="banner banner-info" style="margin: 0">
              Standard moratorium restrictions are maintained by the business and enforced by Duck Creek, not by this portal:
              quote, bind, revise-bind and endorsement blocking, renewal processing and underwriter overrides.
              This portal only determines whether a moratorium applies to a Property location. No override capability is available in this release.
            </div>
          </div>
        </section>

        <div class="actions">
          <button class="btn" type="submit" [disabled]="busy()">{{ id() ? 'Save Changes' : 'Create Moratorium' }}</button>
          @if (!id()) { <button class="btn btn-secondary" type="button" [disabled]="busy()" (click)="submit(true)">Save as Draft</button> }
          <a class="btn btn-secondary" [routerLink]="id() ? ['/moratoriums', id()] : ['/moratoriums']">Cancel</a>
        </div>
      </form>
    } @else if (!error()) {
      <div class="empty">Loading…</div>
    }
  `,
})
export class MoratoriumForm implements OnInit {
  /** Present on /moratoriums/:id/edit. */
  readonly id = input<string>();
  private api = inject(ApiService);
  private router = inject(Router);

  sys = systemTimeZone();
  zones = timeZoneOptions();
  states = signal<StateRef[]>([]);
  ready = signal(false);
  busy = signal(false);
  error = signal<ApiError | null>(null);

  name = '';
  timeZoneId = systemTimeZone();
  activation = '';
  deactivation = '';
  immediate = false;
  scheduleDeactivation = false;
  rows: AreaRow[] = [{ state: '', statewide: false, counties: '', zips: '' }];

  ngOnInit() {
    this.api.states().subscribe({
      next: r => {
        this.states.set(r.items);
        if (this.id()) this.loadExisting(); else { this.immediate = true; this.ready.set(true); }
      },
      error: e => this.error.set(toApiError(e)),
    });
  }

  private loadExisting() {
    this.api.get(this.id()!).subscribe({
      next: m => this.populate(m),
      error: e => this.error.set(toApiError(e)),
    });
  }

  private populate(m: MoratoriumDetail) {
    this.name = m.name;
    this.timeZoneId = m.timeZoneId;
    this.zones = timeZoneOptions(m.timeZoneId);
    this.activation = toLocalInput(m.activationDateTime, m.timeZoneId);
    this.deactivation = toLocalInput(m.deactivationDateTime, m.timeZoneId);
    this.scheduleDeactivation = !!m.deactivationDateTime;
    this.rows = m.areas.length
      ? m.areas.map(a => ({ state: a.state ?? '', statewide: a.statewide, counties: (a.counties ?? []).join(', '), zips: (a.zipCodes ?? []).join(', ') }))
      : [{ state: '', statewide: false, counties: '', zips: '' }];
    this.ready.set(true);
  }

  addRow() { this.rows.push({ state: '', statewide: false, counties: '', zips: '' }); }
  removeRow(i: number) {
    this.rows.splice(i, 1);
    if (this.rows.length === 0) this.addRow();
  }

  generalErrors(): FieldError[] {
    return (this.error()?.validationErrors ?? []).filter(v => !v.field || !/^(name|activationDateTime|deactivationDateTime|areas\[\d+\])/.test(v.field));
  }

  fieldError(field: string): string {
    return (this.error()?.validationErrors ?? []).find(v => v.field === field)?.message ?? '';
  }

  /** Errors reported against areas[i].<part>; part '' is the row itself. */
  rowError(i: number, part: string): string {
    const key = part ? `areas[${i}].${part}` : `areas[${i}]`;
    const matches = (this.error()?.validationErrors ?? []).filter(v => v.field === key).map(v => v.message);
    return matches.join(' ');
  }

  submit(draft: boolean) {
    this.error.set(null);
    const req: MoratoriumRequest = {
      name: this.name,
      timeZoneId: this.timeZoneId,
      activateImmediately: !this.id() && this.immediate && !draft,
      saveAsDraft: draft,
      activationDateTime: this.immediate && !this.id() ? null : this.activation || null,
      deactivationDateTime: this.scheduleDeactivation ? this.deactivation || null : null,
      // Entirely blank rows are dropped; partially filled rows are validated by the API.
      areas: this.rows
        .filter(r => r.state || r.statewide || r.counties.trim() || r.zips.trim())
        .map(r => ({ state: r.state || null, statewide: r.statewide, counties: split(r.counties, false), zipCodes: split(r.zips, true) })),
    };
    this.busy.set(true);
    const call = this.id() ? this.api.update(this.id()!, req) : this.api.create(req);
    call.subscribe({
      next: r => this.router.navigate(['/moratoriums', r.data.moratoriumId], { state: { notice: draft ? 'Moratorium saved as draft.' : r.message } }),
      error: e => { this.busy.set(false); this.error.set(toApiError(e)); window.scrollTo({ top: 0, behavior: 'smooth' }); },
    });
  }
}
