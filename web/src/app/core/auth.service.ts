import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Router } from '@angular/router';
import { tap } from 'rxjs';
import { LoginResponse, UserInfo } from './models';

const KEY = 'mm.session';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private http = inject(HttpClient);
  private router = inject(Router);

  private session = signal<LoginResponse | null>(this.restore());
  readonly user = computed<UserInfo | null>(() => this.session()?.user ?? null);
  readonly isAdmin = computed(() => this.user()?.role === 'Admin');
  readonly token = computed(() => this.session()?.token ?? null);

  login(email: string, password: string) {
    return this.http.post<LoginResponse>('/api/auth/login', { email, password }).pipe(
      tap(r => {
        this.session.set(r);
        try { sessionStorage.setItem(KEY, JSON.stringify(r)); } catch { /* storage unavailable */ }
      }));
  }

  logout() {
    this.session.set(null);
    try { sessionStorage.removeItem(KEY); } catch { /* ignore */ }
    this.router.navigate(['/login']);
  }

  private restore(): LoginResponse | null {
    try {
      const raw = sessionStorage.getItem(KEY);
      if (!raw) return null;
      const s = JSON.parse(raw) as LoginResponse;
      return new Date(s.expiresAt) > new Date() ? s : null;
    } catch { return null; }
  }
}
