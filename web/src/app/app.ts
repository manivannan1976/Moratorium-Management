import { Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthService } from './core/auth.service';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    @if (auth.user(); as u) {
      <header class="topbar">
        <a class="brand" routerLink="/moratoriums"><span class="brand-mark">S|2</span> Broker Portal</a>
        <nav aria-label="Primary">
          <a href="#" class="nav-link" (click)="$event.preventDefault()" title="Provided by another Broker Portal module">Underwriting Guidelines</a>
          <a href="#" class="nav-link" (click)="$event.preventDefault()" title="Provided by another Broker Portal module">User Management</a>
          <a class="nav-link" routerLink="/moratoriums" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: false }">Moratorium</a>
          <a class="nav-link" routerLink="/audit" routerLinkActive="active">Audit History</a>
        </nav>
        <div class="user">
          <span class="user-name">{{ u.displayName }}</span>
          <span class="chip chip-role">{{ u.role === 'Admin' ? 'Moratorium Administrator' : 'Moratorium Viewer' }}</span>
          <button type="button" class="btn btn-ghost" (click)="auth.logout()">Sign out</button>
        </div>
      </header>
    }
    <main class="page"><router-outlet /></main>
  `,
})
export class App {
  protected auth = inject(AuthService);
}
