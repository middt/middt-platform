import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MiddtWorkflowDesignerComponent } from '@middt/angular/workflow';
import { MenuService } from '../shell/menu.service';

/** Runtime visual workflow & task designer; saving a workflow refreshes the side menu. */
@Component({
  selector: 'app-workflow-admin',
  imports: [MiddtWorkflowDesignerComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { style: 'display: contents' },
  template: `<middt-workflow-designer (changed)="refreshMenu()" />`,
})
export class WorkflowAdminPage {
  readonly #menu = inject(MenuService);

  protected refreshMenu(): void {
    void this.#menu.refresh();
  }
}
