import { afterRenderEffect, ChangeDetectionStrategy, Component, computed, effect, ElementRef, inject, input, linkedSignal, output, signal, untracked, viewChildren, ViewEncapsulation } from '@angular/core';
import { ErrorStateMatcher } from '@angular/material/core';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInput, MatInputModule } from '@angular/material/input';
import { MatSelect, MatSelectModule } from '@angular/material/select';
import {
  schemaDisabledFields,
  schemaFields,
  schemaFormValue,
  schemaInitialValues,
  schemaLookupOptions,
  schemaRequiredFields,
  schemaVisibleFields,
  validateSchemaFields,
} from '@middt/ui-core';
import type { ExpressionUser, ExpressionUserInfo, SchemaField, SchemaFieldValue, SchemaLookupOption, WorkflowData } from '@middt/ui-core';
import { AuthService } from '@middt/angular/core';
import { UiLookupCache } from '@middt/angular/ui';
import { JSF_STYLES } from './workflow-styles';

/** Shown below an `x-lookup` select whose options cannot be loaded (same text as JsonSchemaForm.razor). */
export const LOOKUP_LOAD_ERROR = 'Liste yüklenemedi.';

/**
 * Input form of a JSON Schema subset (counterpart of JsonSchemaForm.razor): `type: object` with flat `properties` – string
 * (text, long text, enum, date/date-time, e-mail, pattern), number/integer and boolean; nested objects/arrays fall back to
 * a JSON text area. Properties with `x-lookup` (`{ entity, valueField, textField }`) are selects whose options are the rows of the
 * entity (loaded like the layout `select` lookups; the value type follows the property `type`). Call `validate()` before submitting to show the (Turkish) errors next to the fields and `getValue()` for
 * the body. The value is read when the schema changes (recreate the component to start over with another value).
 * Element ids: `{idPrefix}-{property}`; lookup field wrappers carry `data-lookup="{entity}"`.
 */
@Component({
  selector: 'middt-json-schema-form',
  imports: [MatFormFieldModule, MatInputModule, MatSelectModule, MatCheckboxModule],
  encapsulation: ViewEncapsulation.None,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="middt-jsf">
      @if (fields().length === 0 && emptyText()) {
        <p class="middt-jsf-empty">{{ emptyText() }}</p>
      }
      @for (field of fields(); track field.name) {
        @if (visibleFields().has(field.name)) {
        @let id = idPrefix() + '-' + field.name;
        @let error = errors()[field.name];
        @let isRequired = requiredFields().has(field.name);
        @let isDisabled = disabled() || disabledFields().has(field.name);
        <div class="middt-jsf-field" [class.middt-jsf-invalid]="!!error" [attr.data-field]="field.name" [attr.data-lookup]="field.lookup?.entity ?? null">
          @if (field.kind === 'boolean') {
            <div class="middt-jsf-check">
              <mat-checkbox [id]="id" [checked]="values()[field.name] === true" [disabled]="isDisabled"
                            [attr.aria-describedby]="error ? id + '-error' : null" (change)="set(field, $event.checked)">
                <span class="middt-jsf-label">{{ field.title }}</span>
              </mat-checkbox>
            </div>
          } @else {
            <label [for]="id" class="middt-jsf-label">
              {{ field.title }}
              @if (isRequired) {
                <span class="middt-jsf-required" title="Zorunlu alan">*</span>
              }
            </label>
            <mat-form-field class="middt-jsf-input" subscriptSizing="dynamic">
              @if (field.lookup) {
                <mat-select [id]="id" [value]="text(field)" [disabled]="isDisabled" placeholder="Seçiniz" [aria-label]="field.title"
                            [required]="isRequired" [errorStateMatcher]="matcher(field)" (selectionChange)="set(field, $event.value ?? '')">
                  @if (!isRequired) {
                    <mat-option value="">Seçiniz</mat-option>
                  }
                  @for (option of lookupOptions()[field.name]; track option.value) {
                    <mat-option [value]="option.value">{{ option.text }}</mat-option>
                  }
                </mat-select>
              } @else {
              @switch (field.kind) {
                @case ('longText') {
                  <textarea matInput [id]="id" rows="3" [value]="text(field)" [disabled]="isDisabled" [attr.maxlength]="field.maxLength"
                            [attr.aria-required]="isRequired ? 'true' : null" [errorStateMatcher]="matcher(field)" (input)="set(field, $any($event.target).value)"></textarea>
                }
                @case ('json') {
                  <textarea matInput [id]="id" rows="4" placeholder="JSON" class="middt-jsf-code" [value]="text(field)" [disabled]="isDisabled"
                            [attr.aria-required]="isRequired ? 'true' : null" [errorStateMatcher]="matcher(field)" (input)="set(field, $any($event.target).value)"></textarea>
                }
                @case ('enum') {
                  <mat-select [id]="id" [value]="text(field)" [disabled]="isDisabled" placeholder="Seçiniz" [aria-label]="field.title"
                              [required]="isRequired" [errorStateMatcher]="matcher(field)" (selectionChange)="set(field, $event.value ?? '')">
                    @if (!isRequired) {
                      <mat-option value="">Seçiniz</mat-option>
                    }
                    @for (option of field.enumValues; track option) {
                      <mat-option [value]="option">{{ option }}</mat-option>
                    }
                  </mat-select>
                }
                @case ('date') {
                  <input matInput type="date" [id]="id" [value]="text(field)" [disabled]="isDisabled" [attr.aria-required]="isRequired ? 'true' : null"
                         [errorStateMatcher]="matcher(field)" (input)="set(field, $any($event.target).value)" (change)="set(field, $any($event.target).value)" />
                }
                @case ('dateTime') {
                  <input matInput type="datetime-local" [id]="id" [value]="text(field)" [disabled]="isDisabled" [attr.aria-required]="isRequired ? 'true' : null"
                         [errorStateMatcher]="matcher(field)" (input)="set(field, $any($event.target).value)" (change)="set(field, $any($event.target).value)" />
                }
                @case ('number') {
                  <input matInput type="number" step="any" inputmode="decimal" [id]="id" [value]="text(field)" [disabled]="isDisabled"
                         [attr.min]="field.minimum" [attr.max]="field.maximum" [attr.aria-required]="isRequired ? 'true' : null" [errorStateMatcher]="matcher(field)"
                         (input)="set(field, $any($event.target).value)" />
                }
                @case ('integer') {
                  <input matInput type="number" step="1" inputmode="numeric" [id]="id" [value]="text(field)" [disabled]="isDisabled"
                         [attr.min]="field.minimum" [attr.max]="field.maximum" [attr.aria-required]="isRequired ? 'true' : null" [errorStateMatcher]="matcher(field)"
                         (input)="set(field, $any($event.target).value)" />
                }
                @default {
                  <input matInput [type]="field.format === 'email' ? 'email' : 'text'" [id]="id" [value]="text(field)" [disabled]="isDisabled"
                         [attr.maxlength]="field.maxLength" [attr.aria-required]="isRequired ? 'true' : null" [errorStateMatcher]="matcher(field)"
                         [attr.autocomplete]="field.format === 'email' ? 'email' : null" (input)="set(field, $any($event.target).value)" />
                }
              }
              }
              @if (field.description) {
                <mat-hint class="middt-jsf-help">{{ field.description }}</mat-hint>
              }
              @if (error) {
                <mat-error [id]="id + '-error'" class="middt-jsf-error">{{ error }}</mat-error>
              }
            </mat-form-field>
          }
          @if (lookupErrors()[field.name]; as lookupError) {
            <div class="middt-jsf-error middt-jsf-lookup-error" role="alert">{{ lookupError }}</div>
          }
          @if (field.kind === 'boolean' && field.description) {
            <small class="middt-jsf-help">{{ field.description }}</small>
          }
          @if (field.kind === 'boolean' && error) {
            <div [id]="id + '-error'" class="middt-jsf-error" role="alert">{{ error }}</div>
          }
        </div>
        }
      }
    </div>
  `,
  styles: JSF_STYLES,
})
export class MiddtJsonSchemaFormComponent {
  /** JSON Schema of the object to edit. */
  readonly schema = input<WorkflowData | null | undefined>(null);
  /** Initial value (read when the schema changes). */
  readonly value = input<WorkflowData | null | undefined>(null);
  /** Optional persisted workflow/page data exposed as `data` in `visibleWhen`, `requiredWhen` and `disabledWhen` expressions alongside `ui`. */
  readonly data = input<WorkflowData | null | undefined>(null);
  /** Optional user exposed as `user` in form condition expressions; resolved from `AuthService` when omitted. */
  readonly user = input<ExpressionUserInfo | ExpressionUser | null | undefined>(undefined);
  /** Prefix of the element ids (unique per page when several forms are shown). */
  readonly idPrefix = input('jsf');
  readonly disabled = input(false);
  /** Text shown when the schema has no properties. */
  readonly emptyText = input<string | null>(null);
  /** Current value after every change. */
  readonly valueChange = output<WorkflowData>();

  readonly fields = computed<SchemaField[]>(() => schemaFields(this.schema()));

  /** `true` when the schema declares at least one property. */
  readonly hasFields = computed(() => this.fields().length > 0);

  protected readonly effectiveUser = computed<ExpressionUserInfo | ExpressionUser | null>(() => {
    const explicit = this.user();
    if (explicit !== undefined) {
      return explicit;
    }

    const authUser = this.#auth?.user();
    return authUser ? { id: authUser.id ?? null, name: authUser.name ?? null, roles: authUser.roles } : null;
  });

  protected readonly values = linkedSignal<SchemaField[], Record<string, SchemaFieldValue>>({
    source: this.fields,
    computation: fields => untracked(() => schemaInitialValues(this.schema(), fields, this.value(), this.data())),
  });

  protected readonly visibleFields = computed<ReadonlySet<string>>(() => schemaVisibleFields(this.fields(), this.values(), this.data(), this.effectiveUser()));
  protected readonly requiredFields = computed<ReadonlySet<string>>(() => schemaRequiredFields(this.fields(), this.values(), this.data(), this.effectiveUser()));
  protected readonly disabledFields = computed<ReadonlySet<string>>(() => schemaDisabledFields(this.fields(), this.values(), this.data(), this.effectiveUser()));

  protected readonly errors = linkedSignal<SchemaField[], Record<string, string>>({ source: this.fields, computation: () => ({}) });

  /** Rows of the `x-lookup` entities as options, by field name. */
  readonly #lookupRows = linkedSignal<SchemaField[], Record<string, SchemaLookupOption[]>>({ source: this.fields, computation: () => ({}) });
  /** Loading errors of the `x-lookup` entities, by field name (shown below the select, like JsonSchemaForm.razor). */
  protected readonly lookupErrors = linkedSignal<SchemaField[], Record<string, string>>({ source: this.fields, computation: () => ({}) });

  /** Options of the lookup selects; a prefilled value missing from the loaded rows stays selectable (shown as is). */
  protected readonly lookupOptions = computed<Record<string, SchemaLookupOption[]>>(() => {
    const rows = this.#lookupRows();
    const values = this.values();
    const result: Record<string, SchemaLookupOption[]> = {};
    for (const field of this.fields()) {
      if (field.lookup === null) {
        continue;
      }

      const options = rows[field.name] ?? [];
      const value = values[field.name];
      result[field.name] = typeof value === 'string' && value !== '' && !options.some(o => o.value === value)
        ? [{ value, text: value }, ...options]
        : options;
    }

    return result;
  });

  readonly #validated = signal(false);
  readonly #matchers = new Map<string, ErrorStateMatcher>();
  readonly #host = inject<ElementRef<HTMLElement>>(ElementRef);
  readonly #lookupCache = inject(UiLookupCache);
  readonly #auth = inject(AuthService, { optional: true });
  private readonly inputs = viewChildren(MatInput);
  private readonly selects = viewChildren(MatSelect);

  constructor() {
    void this.#auth?.load();
    // The inputs are not bound to form controls: refresh their error state (red outline, mat-error, aria-invalid) explicitly.
    afterRenderEffect(() => {
      this.errors();
      for (const control of [...this.inputs(), ...this.selects()]) {
        control.updateErrorState();
      }
    });
    effect(() => {
      const fields = this.fields();
      untracked(() => void this.#loadLookups(fields));
    });
  }

  /** Validates all visible fields, shows the errors and returns `true` when the value is valid. */
  validate(): boolean {
    this.#validated.set(true);
    const errors = validateSchemaFields(this.fields(), this.values(), this.data(), this.effectiveUser());
    this.errors.set(errors);
    return Object.keys(errors).length === 0;
  }

  /** Current value of visible fields (empty optional fields and hidden fields are omitted). */
  getValue(): WorkflowData {
    return schemaFormValue(this.fields(), this.values(), this.data(), this.effectiveUser());
  }

  /** Clears the entered values and errors (back to `value`, else the schema defaults). */
  reset(value: WorkflowData | null = null): void {
    this.#validated.set(false);
    this.values.set(schemaInitialValues(this.schema(), this.fields(), value, this.data()));
    this.errors.set({});
  }

  /** Moves the keyboard focus to the first visible field (e.g. when a wizard step form is shown). */
  focusFirst(): void {
    const visible = this.visibleFields();
    const first = this.fields().find(f => visible.has(f.name));
    if (first === undefined) {
      return;
    }

    const element = this.#host.nativeElement.querySelector<HTMLElement>(`[data-field="${CSS.escape(first.name)}"] :is(input, textarea, .mat-mdc-select)`);
    element?.focus();
  }

  /** Loads the options of every `x-lookup` field (rows cached per session by UiLookupCache, shared with the layout selects). */
  async #loadLookups(fields: readonly SchemaField[]): Promise<void> {
    await Promise.all(fields.map(async field => {
      const lookup = field.lookup;
      if (lookup === null) {
        return;
      }

      try {
        const rows = await this.#lookupCache.getAll(lookup.entity);
        if (this.fields() === fields) {
          this.#lookupRows.update(all => ({ ...all, [field.name]: schemaLookupOptions(lookup, rows) }));
          this.lookupErrors.update(all => {
            const next = { ...all };
            delete next[field.name];
            return next;
          });
        }
      } catch {
        if (this.fields() === fields) {
          this.lookupErrors.update(all => ({ ...all, [field.name]: LOOKUP_LOAD_ERROR }));
        }
      }
    }));
  }

  protected text(field: SchemaField): string {
    const value = this.values()[field.name];
    return typeof value === 'string' ? value : '';
  }

  protected set(field: SchemaField, value: SchemaFieldValue): void {
    this.values.update(values => ({ ...values, [field.name]: value }));
    if (this.#validated()) {
      this.errors.set(validateSchemaFields(this.fields(), this.values(), this.data(), this.effectiveUser()));
    } else {
      const visible = this.visibleFields();
      this.errors.update(errors => {
        let changed = false;
        const next = { ...errors };
        for (const key of Object.keys(next)) {
          if (!visible.has(key)) {
            delete next[key];
            changed = true;
          }
        }

        return changed ? next : errors;
      });
    }

    this.valueChange.emit(this.getValue());
  }

  /** Shows a field as invalid while it has a validation error. */
  protected matcher(field: SchemaField): ErrorStateMatcher {
    let matcher = this.#matchers.get(field.name);
    if (matcher === undefined) {
      matcher = { isErrorState: () => this.errors()[field.name] !== undefined };
      this.#matchers.set(field.name, matcher);
    }

    return matcher;
  }
}
