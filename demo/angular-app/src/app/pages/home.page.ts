import { ChangeDetectionStrategy, Component } from '@angular/core';

@Component({
  selector: 'app-home',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h1 class="page-title">Middt Framework Sample (Angular)</h1>
    <p>Sol menüden JSON tabanlı İş Akışlarını veya JSON ekran yerleşimlerini açabilirsiniz. Bu ekranlar Blazor örneğiyle aynı JSON yerleşimlerdir.</p>
  `,
})
export class HomePage {}
