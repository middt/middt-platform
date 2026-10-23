import { ChangeDetectionStrategy, Component, computed, DestroyRef, effect, forwardRef, inject, signal, untracked } from '@angular/core';
import { errorText, isSuccess, UiExpressions } from '@middt/ui-core';
import { UI_DATA_CLIENT } from '@middt/angular/core';
import { UiNodeListComponent } from './node.component';
import { type JsonObject, UiProps, UiWidgetBase } from './widget-base';

/** `record` widget: loads one entity by id (`recordId` template) and renders its children with it as `record`. */
@Component({
  selector: 'middt-ui-record-widget',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [forwardRef(() => UiNodeListComponent)],
  host: { style: 'display: contents' },
  template: `
    @if (loading()) {
      <p class="middt-ui-text middt-ui-text-muted" role="status">Yükleniyor...</p>
    } @else if (record() === null) {
      <div class="middt-ui-alert middt-ui-alert-warning" role="alert">{{ error() ?? stringProp('notFound') ?? 'Kayıt bulunamadı.' }}</div>
    } @else {
      <middt-ui-node-list [nodes]="node().children" [context]="recordContext()!" />
    }
  `,
})
export class UiRecordWidget extends UiWidgetBase {
  readonly #dataClient = inject(UI_DATA_CLIENT);
  readonly #abort = new AbortController();
  #requestAbort: AbortController | null = null;
  #loadedEntity: string | null = null;
  #loadedId: string | null = null;
  #request = 0;

  protected readonly loading = signal(true);
  protected readonly record = signal<JsonObject | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly recordContext = computed(() => {
    const record = this.record();
    return record === null ? null : this.context().with(UiExpressions.record, record);
  });

  constructor() {
    super();
    inject(DestroyRef).onDestroy(() => {
      this.#requestAbort?.abort();
      this.#abort.abort();
    });
    effect(() => {
      const id = this.text('recordId');
      const entity = UiProps.string(this.node(), 'entity') ?? '';
      untracked(() => void this.#load(entity, id));
    });
  }

  async #load(entity: string, id: string): Promise<void> {
    if (entity === this.#loadedEntity && id === this.#loadedId) {
      return;
    }

    this.#loadedEntity = entity;
    this.#loadedId = id;
    const request = ++this.#request;
    this.#requestAbort?.abort();
    this.#requestAbort = null;
    this.record.set(null);
    this.error.set(null);
    if (id.trim() === '') {
      this.loading.set(false);
      return;
    }

    const controller = new AbortController();
    this.#requestAbort = controller;
    const onDestroyAbort = () => controller.abort();
    this.#abort.signal.addEventListener('abort', onDestroyAbort, { once: true });

    this.loading.set(true);
    try {
      const response = await this.#dataClient.getById<JsonObject>(entity, id, controller.signal);
      if (request === this.#request) {
        this.record.set(isSuccess(response) ? (response.data ?? null) : null);
        this.error.set(isSuccess(response) ? null : errorText(response));
      }
    } catch (error) {
      if (!controller.signal.aborted && request === this.#request) {
        this.error.set(error instanceof Error ? error.message : String(error));
      }
    } finally {
      this.#abort.signal.removeEventListener('abort', onDestroyAbort);
      if (request === this.#request) {
        this.loading.set(false);
      }
    }
  }
}
