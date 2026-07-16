import { HttpInterceptorFn, HttpErrorResponse } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';
import { AuthService } from './auth.service';

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthService);
  const header = auth.getAuthHeader();
  const authedReq = header ? req.clone({ setHeaders: { Authorization: header } }) : req;

  return next(authedReq).pipe(
    catchError((err: HttpErrorResponse) => {
      if (err.status === 401) {
        auth.onUnauthorized();
      }
      return throwError(() => err);
    })
  );
};
