import { Routes } from '@angular/router';
import { adminGuard, authGuard } from './core/auth.http';

export const routes: Routes = [
  { path: 'login', loadComponent: () => import('./features/login/login').then(m => m.Login), title: 'Sign in' },
  { path: '', pathMatch: 'full', redirectTo: 'moratoriums' },
  {
    path: 'moratoriums',
    canActivate: [authGuard],
    children: [
      { path: '', loadComponent: () => import('./features/dashboard/dashboard').then(m => m.Dashboard), title: 'Moratoriums' },
      {
        path: 'new', canActivate: [adminGuard], title: 'Create Moratorium',
        loadComponent: () => import('./features/moratorium-form/moratorium-form').then(m => m.MoratoriumForm),
      },
      { path: ':id', loadComponent: () => import('./features/detail/detail').then(m => m.Detail), title: 'Moratorium Details' },
      {
        path: ':id/edit', canActivate: [adminGuard], title: 'Update Moratorium',
        loadComponent: () => import('./features/moratorium-form/moratorium-form').then(m => m.MoratoriumForm),
      },
      { path: ':id/audit', loadComponent: () => import('./features/audit/audit').then(m => m.Audit), title: 'Audit History' },
    ],
  },
  { path: 'audit', canActivate: [authGuard], loadComponent: () => import('./features/audit/audit').then(m => m.Audit), title: 'Audit History' },
  { path: '**', redirectTo: 'moratoriums' },
];
