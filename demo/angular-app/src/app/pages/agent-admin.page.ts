import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MiddtAgentDesignerComponent } from '@middt/angular/workflow';
import { MenuService } from '../shell/menu.service';

/** Runtime visual AI Agent, Tool & MCP studio with live playground. */
@Component({
  selector: 'app-agent-admin',
  imports: [MiddtAgentDesignerComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { style: 'display: contents' },
  template: `<middt-agent-designer (changed)="refreshMenu()" />`,
})
export class AgentAdminPage {
  readonly #menu = inject(MenuService);

  protected refreshMenu(): void {
    void this.#menu.refresh();
  }
}

