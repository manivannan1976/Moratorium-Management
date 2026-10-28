import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { AuthService } from '../../core/auth.service';
import { toApiError } from '../../core/api.service';

@Component({
  selector: 'app-login',
  imports: [FormsModule],
  template: `
    <div class="card" style="max-width: 420px; margin: 8vh auto 0">
      <div class="card-head"><h1>Moratorium Management</h1></div>
      <form class="card-body" (ngSubmit)="submit()" #f="ngForm">
        <p class="muted" style="margin-top: 0">Sign in to the Broker Portal.</p>
        @if (error()) { <div class="banner banner-error" role="alert">{{ error() }}</div> }
        <label class="field"><span class="lbl req">Email</span>
          <input type="email" name="email" [(ngModel)]="email" required autocomplete="username"></label>
        <label class="field"><span class="lbl req">Password</span>
          <input type="password" name="password" [(ngModel)]="password" required autocomplete="current-password"></label>
        <button class="btn" type="submit" [disabled]="busy() || f.invalid" style="width: 100%; justify-content: center">
          {{ busy() ? 'Signing in…' : 'Sign in' }}
        </button>
        <div class="hint" style="margin-top: 14px">
          Local demo accounts:<br>
          Administrator — admin&#64;portal.local / Admin&#64;12345<br>
          Viewer — viewer&#64;portal.local / Viewer&#64;12345
        </div>
      </form>
    </div>
  `,
})
export class Login {
  private auth = inject(AuthService);
  private router = inject(Router);
  private route = inject(ActivatedRoute);

  email = '';
  password = '';
  busy = signal(false);
  error = signal('');

  submit() {
    this.busy.set(true);
    this.error.set('');
    this.auth.login(this.email, this.password).subscribe({
      next: () => this.router.navigateByUrl(this.route.snapshot.queryParamMap.get('returnUrl') || '/moratoriums'),
      error: e => { this.busy.set(false); this.error.set(toApiError(e).message); },
    });
  }
}
