// Display widgets – same markup and CSS classes as the Blazor display widgets and the static UiHtmlRenderer.

import { ChangeDetectionStrategy, Component, computed, forwardRef, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { Router } from '@angular/router';
import { PortableExpressionError, UiExpressions, UiFieldJson, type UiNode, UiRenderContext, UiUrl } from '@middt/ui-core';
import { UiNodeComponent, UiNodeListComponent } from './node.component';
import { cloneJson, isJsonObject, UiProps, UiWidgetBase } from './widget-base';

const contents = { style: 'display: contents' };

@Component({
  selector: 'middt-ui-heading',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: contents,
  template: `
    @switch (level()) {
      @case (1) { <h1 class="middt-ui-heading">{{ text('text') }}</h1> }
      @case (2) { <h2 class="middt-ui-heading">{{ text('text') }}</h2> }
      @case (3) { <h3 class="middt-ui-heading">{{ text('text') }}</h3> }
      @case (4) { <h4 class="middt-ui-heading">{{ text('text') }}</h4> }
      @case (5) { <h5 class="middt-ui-heading">{{ text('text') }}</h5> }
      @default { <h6 class="middt-ui-heading">{{ text('text') }}</h6> }
    }
  `,
})
export class UiHeadingWidget extends UiWidgetBase {
  protected readonly level = computed(() => Math.min(6, Math.max(1, UiProps.int(this.node(), 'level', 2))));
}

@Component({
  selector: 'middt-ui-text',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: contents,
  template: `<p class="middt-ui-text middt-ui-text-{{ stringProp('style') ?? 'normal' }}">{{ text('text') }}</p>`,
})
export class UiTextWidget extends UiWidgetBase {}

@Component({
  selector: 'middt-ui-fields',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: contents,
  template: `
    <dl class="middt-ui-fields">
      @for (item of items(); track $index) {
        <div class="middt-ui-field">
          <dt>{{ item.label }}</dt>
          <dd>{{ item.value }}</dd>
        </div>
      }
    </dl>
  `,
})
export class UiFieldsWidget extends UiWidgetBase {
  protected readonly items = computed(() => {
    const context = this.context();
    return (UiProps.array(this.node(), 'items') ?? [])
      .filter(isJsonObject)
      .filter(item => context.isVisible(UiFieldJson.getString(item, 'visibleWhen')))
      .map(item => ({
        label: UiFieldJson.getString(item, 'label') ?? '',
        value: context.text(UiFieldJson.getString(item, 'value'), UiFieldJson.getString(item, 'format')),
      }));
  });
}

@Component({
  selector: 'middt-ui-alert',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: contents,
  template: `<div class="middt-ui-alert middt-ui-alert-{{ severity() }}" [attr.role]="severity() === 'danger' || severity() === 'warning' ? 'alert' : 'status'">{{ text('text') }}</div>`,
})
export class UiAlertWidget extends UiWidgetBase {
  protected readonly severity = computed(() => UiProps.string(this.node(), 'severity') ?? 'info');
}

@Component({
  selector: 'middt-ui-badge',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: contents,
  template: `<span class="middt-ui-badge middt-ui-badge-{{ stringProp('color') ?? 'secondary' }}">{{ text('text') }}</span>`,
})
export class UiBadgeWidget extends UiWidgetBase {}

@Component({
  selector: 'middt-ui-stat',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: contents,
  template: `
    <div class="middt-ui-stat middt-ui-stat-{{ stringProp('color') ?? 'primary' }}">
      <div class="middt-ui-stat-header">
        <span class="middt-ui-stat-label">{{ text('label') }}</span>
        @if (icon(); as ic) {
          <span class="middt-ui-stat-icon" aria-hidden="true">{{ ic }}</span>
        }
      </div>
      <div class="middt-ui-stat-value">{{ formattedValue() }}</div>
      @if (hasSubtitle()) {
        <div class="middt-ui-stat-subtitle">{{ text('subtitle') }}</div>
      }
    </div>
  `,
})
export class UiStatWidget extends UiWidgetBase {
  protected readonly icon = computed(() => {
    const ic = UiProps.string(this.node(), 'icon');
    return ic && ic.length > 0 ? ic : null;
  });

  protected readonly formattedValue = computed(() =>
    this.context().text(UiProps.string(this.node(), 'value'), UiProps.string(this.node(), 'format')),
  );

  protected readonly hasSubtitle = computed(() => UiProps.string(this.node(), 'subtitle') !== null);
}

@Component({
  selector: 'middt-ui-tabs',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [forwardRef(() => UiNodeComponent)],
  host: contents,
  template: `
    @if (tabs().length > 0) {
      <div class="middt-ui-tabs">
        <div class="middt-ui-tablist" role="tablist">
          @for (tab of tabs(); track $index) {
            <button
              type="button"
              role="tab"
              [class]="$index === active() ? 'middt-ui-tab middt-ui-tab-active' : 'middt-ui-tab'"
              [attr.aria-selected]="$index === active() ? 'true' : 'false'"
              (click)="selectTab($index)">
              {{ tab.title }}
            </button>
          }
        </div>
        @for (tab of tabs(); track $index) {
          <div
            [class]="$index === active() ? 'middt-ui-tabpanel' : 'middt-ui-tabpanel middt-ui-tabpanel-hidden'"
            role="tabpanel">
            <middt-ui-node [node]="tab.child" [context]="context()" />
          </div>
        }
      </div>
    }
  `,
})
export class UiTabsWidget extends UiWidgetBase {
  protected readonly activeIndex = signal(0);

  protected readonly tabs = computed(() => {
    const context = this.context();
    const node = this.node();
    const titles = UiProps.array(node, 'titles');
    const result: { child: UiNode; title: string }[] = [];
    const children = node.children ?? [];
    for (let i = 0; i < children.length; i++) {
      const child = children[i];
      if (!context.isVisible(child.visibleWhen ?? null)) {
        continue;
      }

      const rawExplicit = titles !== null && i < titles.length ? titles[i] : null;
      const explicitTitle = typeof rawExplicit === 'string' || typeof rawExplicit === 'number' ? String(rawExplicit) : null;
      const childTitle = UiProps.string(child, 'title');
      const resolvedTitle =
        explicitTitle && explicitTitle.trim().length > 0
          ? context.text(explicitTitle)
          : childTitle && childTitle.length > 0
            ? context.text(childTitle)
            : `Sekme ${result.length + 1}`;
      result.push({ child, title: resolvedTitle });
    }

    return result;
  });

  protected readonly active = computed(() => {
    const count = this.tabs().length;
    return count === 0 ? 0 : Math.min(Math.max(0, this.activeIndex()), count - 1);
  });

  protected selectTab(index: number): void {
    this.activeIndex.set(index);
  }
}

@Component({
  selector: 'middt-ui-timeline',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: contents,
  template: `
    @if (entries().length === 0) {
      <p class="middt-ui-text middt-ui-text-muted">{{ stringProp('empty') ?? 'Kayıt yok.' }}</p>
    } @else {
      <ol class="middt-ui-timeline">
        @for (entry of entries(); track $index) {
          <li class="middt-ui-timeline-item">
            <span class="middt-ui-timeline-dot middt-ui-timeline-dot-{{ entry.color }}" aria-hidden="true"></span>
            <div class="middt-ui-timeline-content">
              <div class="middt-ui-timeline-header">
                <strong class="middt-ui-timeline-title">{{ entry.title }}</strong>
                @if (entry.time) {
                  <span class="middt-ui-timeline-time">{{ entry.time }}</span>
                }
              </div>
              @if (entry.subtitle) {
                <div class="middt-ui-timeline-subtitle">{{ entry.subtitle }}</div>
              }
            </div>
          </li>
        }
      </ol>
    }
  `,
})
export class UiTimelineWidget extends UiWidgetBase {
  protected readonly entries = computed(() => {
    const context = this.context();
    const node = this.node();
    const defaultColor = UiProps.string(node, 'color') ?? 'primary';
    const list: { title: string; subtitle: string | null; time: string | null; color: string }[] = [];

    const staticItems = UiProps.array(node, 'items');
    if (staticItems !== null && staticItems.length > 0) {
      for (const item of staticItems) {
        if (!isJsonObject(item) || !context.isVisible(UiFieldJson.getString(item, 'visibleWhen'))) {
          continue;
        }

        const title = context.text(UiFieldJson.getString(item, 'title'));
        const subRaw = UiFieldJson.getString(item, 'subtitle');
        const subtitle = subRaw !== null ? context.text(subRaw) : null;
        const timeRaw = UiFieldJson.getString(item, 'time');
        const time = timeRaw !== null ? context.text(timeRaw) : null;
        const color = UiFieldJson.getString(item, 'color') ?? defaultColor;
        list.push({ title, subtitle, time, color });
      }

      return list;
    }

    const source = UiProps.string(node, 'source');
    if (source !== null && source.length > 0) {
      let value: unknown;
      try {
        value = context.render(`{{ ${source} }}`);
      } catch (error) {
        if (error instanceof PortableExpressionError) {
          return [];
        }

        throw error;
      }

      if (Array.isArray(value)) {
        const titleTpl = UiProps.string(node, 'title');
        const subTpl = UiProps.string(node, 'subtitle');
        const timeTpl = UiProps.string(node, 'time');
        for (let idx = 0; idx < Math.min(value.length, MAX_LIST_ITEMS); idx++) {
          const cloned = cloneJson(value[idx]);
          if (isJsonObject(cloned) && !('rowNumber' in cloned)) {
            cloned['rowNumber'] = idx + 1;
          }

          const rowContext = context.with(UiExpressions.row, cloned);
          const title = rowContext.text(titleTpl);
          const subtitle = subTpl !== null ? rowContext.text(subTpl) : null;
          const time = timeTpl !== null ? rowContext.text(timeTpl) : null;
          const rowColor = isJsonObject(cloned) ? UiFieldJson.getString(cloned, 'color') : null;
          const color = rowColor && rowColor.length > 0 ? rowColor : defaultColor;
          list.push({ title, subtitle, time, color });
        }
      }
    }

    return list;
  });
}

@Component({
  selector: 'middt-ui-section',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [forwardRef(() => UiNodeListComponent)],
  host: contents,
  template: `
    <section class="middt-ui-section" [attr.aria-labelledby]="hasTitle() ? elementId('title') : null">
      @if (hasTitle()) {
        <h3 class="middt-ui-section-title" [id]="elementId('title')">{{ text('title') }}</h3>
      }
      <middt-ui-node-list [nodes]="node().children" [context]="context()" />
    </section>
  `,
})
export class UiSectionWidget extends UiWidgetBase {
  protected readonly hasTitle = computed(() => UiProps.string(this.node(), 'title') !== null);
}

@Component({
  selector: 'middt-ui-columns',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [forwardRef(() => UiNodeComponent)],
  host: contents,
  template: `
    <div class="middt-ui-columns">
      @for (child of node().children ?? []; track $index) {
        <div class="middt-ui-column" [style.flex]="widths()[$index] + ' 1 0'">
          <middt-ui-node [node]="child" [context]="context()" />
        </div>
      }
    </div>
  `,
})
export class UiColumnsWidget extends UiWidgetBase {
  /** Flex grow factor per child. */
  protected readonly widths = computed(() => {
    const node = this.node();
    const widths = UiProps.array(node, 'widths');
    return (node.children ?? []).map((_, index) => {
      const value = widths !== null && index < widths.length ? widths[index] : null;
      const width = typeof value === 'string' || typeof value === 'number' ? UiFieldJson.toDecimal(value) : null;
      return width !== null && width > 0 ? String(width) : '1';
    });
  });
}

@Component({
  selector: 'middt-ui-divider',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: contents,
  template: `<hr class="middt-ui-divider" />`,
})
export class UiDividerWidget extends UiWidgetBase {}

const MAX_LIST_ITEMS = 1000;

@Component({
  selector: 'middt-ui-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [forwardRef(() => UiNodeListComponent), MatButtonModule, MatIconModule],
  host: contents,
  template: `
    @if (items().length === 0) {
      <p class="middt-ui-text middt-ui-text-muted">{{ stringProp('empty') ?? 'Kayıt yok.' }}</p>
    } @else {
      <ul class="middt-ui-list">
        @for (item of items(); track $index) {
          <li>
            <middt-ui-node-list [nodes]="node().children" [context]="item" />
            @if (rowTransitions().length > 0) {
              <div class="middt-ui-toolbar">
                @for (tr of rowTransitions(); track tr.key) {
                  <button mat-stroked-button type="button" [class.middt-ui-danger]="tr.style === 'danger'"
                          [attr.data-transition]="tr.key" (click)="executeRowTransition(tr, item)">
                    @if (tr.icon) {
                      <mat-icon>{{ tr.icon }}</mat-icon>
                    }
                    {{ tr.text }}
                  </button>
                }
              </div>
            }
          </li>
        }
      </ul>
    }
  `,
})
export class UiListWidget extends UiWidgetBase {
  /** One context per item (`row`). */
  protected readonly items = computed(() => {
    const context = this.context();
    const source = UiProps.string(this.node(), 'source');
    if (source === null) {
      return [];
    }

    let value: unknown;
    try {
      value = context.render(`{{ ${source} }}`);
    } catch (error) {
      if (error instanceof PortableExpressionError) {
        return [];
      }

      throw error;
    }

    return Array.isArray(value)
      ? value.slice(0, MAX_LIST_ITEMS).map((item, idx) => {
          const cloned = cloneJson(item);
          if (isJsonObject(cloned) && !('rowNumber' in cloned)) {
            cloned['rowNumber'] = idx + 1;
          }
          return context.with(UiExpressions.row, cloned);
        })
      : [];
  });

  protected readonly rowTransitions = computed(() => {
    const context = this.context();
    const node = this.node();
    const candidates: Record<string, unknown>[] = [];
    const single = UiProps.object(node, 'rowTransition');
    if (single !== null) {
      candidates.push(single);
    }

    const multiple = UiProps.array(node, 'rowTransitions');
    if (multiple !== null) {
      for (const item of multiple) {
        if (isJsonObject(item)) {
          candidates.push(item);
        }
      }
    }

    return candidates
      .map(obj => {
        const key = UiFieldJson.getString(obj, 'key');
        if (key === null || !context.canExecuteTransition(key)) {
          return null;
        }

        return {
          key,
          text: UiFieldJson.getString(obj, 'text') ?? key,
          icon: UiFieldJson.getString(obj, 'icon'),
          style: UiFieldJson.getString(obj, 'style') ?? 'primary',
          inputTemplate: isJsonObject(obj['input']) ? obj['input'] : null,
        };
      })
      .filter((x): x is NonNullable<typeof x> => x !== null);
  });

  protected executeRowTransition(
    tr: { key: string; inputTemplate: Record<string, unknown> | null },
    rowContext: UiRenderContext,
  ): void {
    const execute = this.context().executeTransition;
    if (execute === undefined) {
      return;
    }

    const rendered = tr.inputTemplate === null ? null : rowContext.render(tr.inputTemplate);
    void execute(tr.key, isJsonObject(rendered) ? rendered : null);
  }
}

@Component({
  selector: 'middt-ui-link',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: contents,
  template: `
    @if (safe()) {
      <a [class]="stringProp('style') === 'button' ? 'middt-ui-button' : 'middt-ui-link'" [attr.href]="href()" (click)="navigate($event)">{{ text('text') }}</a>
    } @else {
      <span class="middt-ui-link-disabled">{{ text('text') }}</span>
    }
  `,
})
export class UiLinkWidget extends UiWidgetBase {
  readonly #router = inject(Router);

  protected readonly href = computed(() => this.text('href'));
  protected readonly safe = computed(() => UiUrl.isSafe(this.href()));

  /** Local links navigate inside the application (like Blazor's link interception); others load normally. */
  protected navigate(event: MouseEvent): void {
    const href = this.href();
    if (UiUrl.isLocal(href) && event.button === 0 && !event.ctrlKey && !event.metaKey && !event.shiftKey && !event.altKey) {
      event.preventDefault();
      void this.#router.navigateByUrl(href);
    }
  }
}
