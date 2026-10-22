import { ChangeDetectionStrategy, Component, computed, inject, input, OnInit, output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import {
  errorText,
  isSuccess,
  UiExpressions,
  type UiLayoutDocument,
  type UiLayoutRevision,
  type UiLayoutSummary,
  type UiMenu,
  type UiNode,
  type UiRenderContext,
  type UiWidgetSpecModel,
} from '@middt/ui-core';
import { MIDDT_CONFIG, NotifyService, UI_API_CLIENT } from '@middt/angular/core';
import { confirmDialog } from './dialogs';
import { UiLayoutCache } from './layout-cache.service';
import { parseLayout, serializeLayout } from './layout-json';
import { UiLayoutViewComponent } from './node.component';
import { UiRenderContextFactory } from './render-context';

interface WidgetPaletteItem {
  readonly category: string;
  readonly widget: string;
  readonly icon: string;
  readonly title: string;
  readonly description: string;
}

export interface WorkflowSchemaFieldHint {
  readonly field: string;
  readonly title: string;
  readonly type: string;
}

const PALETTE_ITEMS: readonly WidgetPaletteItem[] = [
  {
    category: '📦 Düzen & Kapsayıcılar',
    widget: 'section',
    icon: '🗂️',
    title: 'Kart / Bölüm (section)',
    description: 'Başlıklı kart kapsayıcısı; içine diğer bileşenler sürüklenir.',
  },
  {
    category: '📦 Düzen & Kapsayıcılar',
    widget: 'columns',
    icon: '🏛️',
    title: 'Yan Yana Sütunlar (columns)',
    description: 'Alt bileşenlerini yan yana sütunlar halinde gösterir.',
  },
  {
    category: '📦 Düzen & Kapsayıcılar',
    widget: 'list',
    icon: '🔁',
    title: 'Tekrarlayan Liste (list)',
    description: 'Bir dizi (data.items) içindeki her kayıt için alt bileşenleri tekrarlar.',
  },
  {
    category: '📦 Düzen & Kapsayıcılar',
    widget: 'record',
    icon: '🔍',
    title: 'Kayıt Yükleyici (record)',
    description: 'Tekil bir varlık kaydını (entity/recordId) yükler.',
  },
  {
    category: '📦 Düzen & Kapsayıcılar',
    widget: 'layout',
    icon: '🧩',
    title: 'Alt Ekran Gömme (layout)',
    description: "Başka bir 'view' ekranını bu ekranın içine gömer.",
  },
  {
    category: '📝 İçerik & Göstergeler',
    widget: 'fields',
    icon: '📋',
    title: 'Etiket / Değer Tablosu (fields)',
    description: 'Müşteri adı, tutar, durum gibi etiket-değer alanlarını listeler.',
  },
  {
    category: '📝 İçerik & Göstergeler',
    widget: 'heading',
    icon: '🔤',
    title: 'Başlık (heading)',
    description: 'H1–H6 seviyesinde dinamik başlık metni.',
  },
  {
    category: '📝 İçerik & Göstergeler',
    widget: 'text',
    icon: '📄',
    title: 'Metin / Paragraf (text)',
    description: 'Açıklama, kod veya bilgi paragrafı.',
  },
  {
    category: '📝 İçerik & Göstergeler',
    widget: 'alert',
    icon: '⚠️',
    title: 'Uyarı Kutusu (alert)',
    description: 'Bilgi, başarı, uyarı veya hata mesaj kutusu.',
  },
  {
    category: '📝 İçerik & Göstergeler',
    widget: 'badge',
    icon: '🏷️',
    title: 'Durum Rozeti (badge)',
    description: 'Renkli durum etiketi (Onaylandı, Bekliyor vb.).',
  },
  {
    category: '📝 İçerik & Göstergeler',
    widget: 'divider',
    icon: '➖',
    title: 'Ayırıcı Çizgi (divider)',
    description: 'Bölümler arası yatay çizgi.',
  },
  {
    category: '📝 İçerik & Göstergeler',
    widget: 'link',
    icon: '🔗',
    title: 'Bağlantı / Buton (link)',
    description: 'Sayfa içi veya harici yönlendirme bağlantısı.',
  },
  {
    category: '📊 Veri Tablosu',
    widget: 'grid',
    icon: '📊',
    title: 'Veri Tablosu (grid)',
    description: 'Sunucu sayfalı, arama filtreli ve CRUD destekli tablo.',
  },
];

/**
 * 2D Drag-and-Drop Visual Screen & Layout Studio (`<middt-layout-editor />`).
 * Works both standalone at `/ui-admin` and embedded inside `<middt-workflow-designer />`.
 */
@Component({
  selector: 'middt-layout-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, MatCardModule, MatIconModule, UiLayoutViewComponent],
  template: `
    <div class="middt-wf-studio" data-testid="ui-layout-designer">
      <mat-card appearance="outlined">
        <mat-card-content>
          <div class="middt-ui-toolbar" style="justify-content: space-between">
            <div class="middt-ui-toolbar">
              <label for="ui-layout-select" style="font-weight: 600; font-size: .88rem">Ekran / Yerleşim:</label>
              <select
                id="ui-layout-select"
                class="middt-wf-input"
                style="width: 240px"
                [value]="selectedKey() ?? ''"
                (change)="onSelectLayout($any($event.target).value)">
                @if (selectedKey() === null) {
                  <option value="">-- Yeni Yerleşim ({{ doc().key }}) --</option>
                }
                @for (item of layouts(); track item.key) {
                  <option [value]="item.key">{{ item.title ?? item.key }} ({{ item.key }} · {{ item.kind }} · v{{ item.version }})</option>
                }
              </select>
              <button mat-stroked-button type="button" (click)="newLayout('page')">
                <mat-icon>add</mat-icon> Yeni Sayfa
              </button>
              <button mat-stroked-button type="button" (click)="newLayout('view')">
                <mat-icon>view_quilt</mat-icon> Yeni Akış Ekranı
              </button>
              <span
                class="middt-ui-badge"
                [class]="doc().kind === 'view' ? 'middt-ui-badge-primary' : 'middt-ui-badge-info'">
                {{ doc().kind === 'view' ? 'view (Akış / Parça)' : 'page (Tam Sayfa)' }}
              </span>
              @if (version() > 0) {
                <span class="middt-ui-badge middt-ui-badge-secondary">v{{ version() }}</span>
              }
            </div>

            <div class="middt-ui-toolbar">
              @if (workflowFieldsInput().length > 0) {
                <button
                  mat-stroked-button
                  type="button"
                  title="İş akışının dataSchema alanlarından otomatik Bölüm + Etiket/Değer tablosu oluşturur"
                  (click)="generateSectionFromWorkflowSchema()">
                  <mat-icon>auto_fix_high</mat-icon> Şemadan Alanları Ekle
                </button>
              }
              <button mat-button type="button" (click)="format()">
                <mat-icon>data_object</mat-icon> Biçimlendir
              </button>
              <button mat-stroked-button type="button" (click)="validate()">
                <mat-icon>rule</mat-icon> Doğrula
              </button>
              <button mat-stroked-button type="button" (click)="previewDraft()">
                <mat-icon>visibility</mat-icon> Önizle
              </button>
              <button mat-flat-button color="primary" type="button" [disabled]="busy()" (click)="save()">
                <mat-icon>save</mat-icon> Kaydet
              </button>
              @if (selectedKey() !== null) {
                <button mat-flat-button type="button" class="middt-ui-danger" [disabled]="busy()" (click)="remove()">
                  <mat-icon>delete</mat-icon> Sil
                </button>
              }
              @if (embedded()) {
                <button mat-stroked-button type="button" (click)="closed.emit()">
                  <mat-icon>close</mat-icon> Akışa Dön
                </button>
              }
            </div>
          </div>

          @if (errors().length > 0) {
            <div class="middt-ui-alert middt-ui-alert-danger" style="margin-top: .75rem" role="alert">
              <strong>{{ errors().length }} hata</strong>
              <ul class="middt-ui-list">
                @for (e of errors(); track $index) {
                  <li>{{ e }}</li>
                }
              </ul>
            </div>
          } @else if (validMessage(); as msg) {
            <div class="middt-ui-alert middt-ui-alert-success" style="margin-top: .75rem" role="status">{{ msg }}</div>
          }
        </mat-card-content>
      </mat-card>

      <div class="middt-wf-studio-body">
        <!-- 1. Left Column: Draggable Widget Palette + Workflow Fields + Saved Layouts -->
        <aside class="middt-wf-palette" aria-label="Ekran bileşen paleti ve yerleşimler">
          <div class="middt-wf-palette-group">
            <div class="middt-wf-palette-group-title">🧩 Bileşen Paleti (Sürükle & Bırak)</div>
            <input
              class="middt-wf-input"
              placeholder="Bileşen ara (section, fields, alert...)"
              [value]="paletteFilter()"
              (input)="paletteFilter.set($any($event.target).value)" />
            @for (grp of filteredPaletteGroups(); track grp.category) {
              <div class="middt-wf-palette-group-title">{{ grp.category }}</div>
              @for (item of grp.items; track item.widget) {
                <div
                  class="middt-wf-palette-chip"
                  draggable="true"
                  [title]="item.description + ' — Tuvale sürükleyin veya çift tıklayın'"
                  (dragstart)="startPaletteWidgetDrag(item.widget)"
                  (dblclick)="quickAddWidget(item.widget)">
                  <span class="middt-wf-palette-chip-icon">{{ item.icon }}</span>
                  <div class="middt-wf-palette-chip-text">
                    <strong>{{ item.title }}</strong>
                    <small>{{ item.widget }}</small>
                  </div>
                </div>
              }
            }
          </div>

          @if (workflowFieldsInput().length > 0) {
            <hr class="middt-ui-divider" />
            <div class="middt-wf-palette-group">
              <div class="middt-wf-palette-group-title">🗂️ Akış Veri Alanları</div>
              <small class="middt-ui-text-muted">Tıklayarak seçili <code>fields</code> bileşenine veya tuvale ekleyin:</small>
              @for (wfField of workflowFieldsInput(); track wfField.field) {
                <button
                  type="button"
                  class="middt-wf-task-pill"
                  (click)="insertWorkflowField(wfField)">
                  <span>➕ {{ wfField.title }}</span>
                  <small><code>data.{{ wfField.field }}</code></small>
                </button>
              }
            </div>
          }

          <hr class="middt-ui-divider" />
          <details [open]="!embedded()">
            <summary style="font-weight: 700; font-size: .78rem; cursor: pointer">
              📂 Kayıtlı Yerleşimler ({{ layouts().length }})
            </summary>
            <ul class="middt-ui-admin-items" aria-label="Yerleşimler">
              @for (item of layouts(); track item.key) {
                <li>
                  <button
                    type="button"
                    class="middt-ui-admin-item"
                    [class.middt-ui-admin-item-active]="item.key === selectedKey()"
                    [attr.aria-current]="item.key === selectedKey() ? 'true' : null"
                    (click)="open(item.key)">
                    <span>{{ item.title ?? item.key }}</span>
                    <small>{{ item.key }} · {{ item.kind }} · v{{ item.version }}</small>
                  </button>
                </li>
              }
            </ul>
          </details>
        </aside>

        <!-- 2. Center Column: Visual Screen Tree + Live Preview + JSON Code -->
        <div style="display: flex; flex-direction: column; gap: .75rem; min-width: 0">
          <div
            class="middt-ui-studio-canvas"
            (dragover)="$event.preventDefault()"
            (drop)="onDropIntoContainer('')">
            <div
              class="middt-ui-toolbar"
              style="justify-content: space-between; border-bottom: 1px solid #e2e8f0; padding-bottom: .5rem">
              <div style="display: flex; align-items: center; gap: .5rem; cursor: pointer" (click)="selectedPath.set(null)">
                <span class="middt-ui-badge middt-ui-badge-info">🖥️ Ekran Gövdesi (body)</span>
                <strong>{{ doc().title || doc().key }}</strong>
                <code class="middt-ui-text-code">({{ doc().key }})</code>
              </div>
              <button
                mat-stroked-button
                type="button"
                (click)="selectedPath.set(null)">
                <mat-icon>settings</mat-icon> Ekran Ayarları
              </button>
            </div>

            @if (doc().body.length === 0) {
              <div class="middt-wf-dropzone" style="padding: 2rem; text-align: center">
                <div class="middt-wf-dropzone-empty" style="font-size: .88rem">
                  Sol paletten bir bileşeni (ör. <strong>Kart / Bölüm</strong>, <strong>Yan Yana Sütunlar</strong>,
                  <strong>Etiket / Değer Tablosu</strong>) buraya sürükleyip bırakın veya çift tıklayın.
                </div>
              </div>
            } @else {
              @for (node of doc().body; track $index) {
                @let topPath = '' + $index;
                <div
                  [class]="
                    selectedPath() === topPath
                      ? 'middt-ui-widget-card middt-ui-widget-card-selected'
                      : 'middt-ui-widget-card'
                  "
                  draggable="true"
                  (click)="$event.stopPropagation(); selectedPath.set(topPath)"
                  (dragstart)="$event.stopPropagation(); startExistingNodeDrag(topPath)">
                  <div class="middt-ui-toolbar" style="justify-content: space-between">
                    <div style="display: flex; align-items: center; gap: .45rem; min-width: 0">
                      <span>{{ getWidgetIcon(node.widget) }}</span>
                      <span class="middt-ui-badge middt-ui-badge-primary" style="font-size: .72rem">{{ node.widget }}</span>
                      <strong style="font-size: .86rem; overflow: hidden; text-overflow: ellipsis; white-space: nowrap">
                        {{ getWidgetSummary(node) }}
                      </strong>
                      @if (node.visibleWhen) {
                        <span class="middt-ui-badge middt-ui-badge-warning" style="font-size: .68rem">
                          👁️ {{ node.visibleWhen }}
                        </span>
                      }
                    </div>
                    <div class="middt-ui-toolbar" (click)="$event.stopPropagation()">
                      @if (node.widget === 'layout' && getNodeString(node, 'key')) {
                        <button mat-button type="button" (click)="open(getNodeString(node, 'key'))">
                          ↗ {{ getNodeString(node, 'key') }}
                        </button>
                      }
                      <button mat-button type="button" [disabled]="$index <= 0" (click)="moveRootNode($index, -1)">↑</button>
                      <button
                        mat-button
                        type="button"
                        [disabled]="$index >= doc().body.length - 1"
                        (click)="moveRootNode($index, 1)">↓</button>
                      <button mat-button type="button" (click)="duplicateRootNode($index)">📑</button>
                      <button mat-button color="warn" type="button" (click)="deleteRootNode($index)">Sil</button>
                    </div>
                  </div>

                  @if (node.widget === 'fields') {
                    <div style="display: flex; flex-wrap: wrap; gap: .35rem; margin-top: .15rem">
                      @for (fItem of getNodeArray(node, 'items'); track $index) {
                        <span class="middt-wf-edge-pill" style="width: auto; font-size: .73rem; padding: .15rem .45rem">
                          <strong>{{ fItem['label'] || 'Alan' }}:</strong>
                          <code>{{ fItem['value'] || '' }}</code>
                        </span>
                      }
                    </div>
                  }

                  @if (isContainerWidget(node.widget)) {
                    <div
                      class="middt-ui-widget-children"
                      (dragover)="$event.preventDefault(); $event.stopPropagation()"
                      (drop)="$event.stopPropagation(); onDropIntoContainer(topPath)">
                      <div class="middt-ui-toolbar" style="justify-content: space-between">
                        <small class="middt-ui-text-muted">
                          Alt Bileşenler ({{ (node.children ?? []).length }}) — Paletten buraya bileşen sürükleyin
                        </small>
                        <select
                          class="middt-wf-input"
                          style="width: 170px; padding: .15rem .35rem; font-size: .75rem"
                          value=""
                          (change)="addChildWidget(node, topPath, $any($event.target).value); $any($event.target).value = ''">
                          <option value="">+ Alt Bileşen Ekle...</option>
                          @for (p of paletteItems; track p.widget) {
                            <option [value]="p.widget">{{ p.icon }} {{ p.title }}</option>
                          }
                        </select>
                      </div>

                      @if ((node.children ?? []).length > 0) {
                        <div [class]="node.widget === 'columns' ? 'middt-ui-widget-columns-grid' : 'middt-ui-form'">
                          @for (child of node.children ?? []; track $index) {
                            @let childPath = topPath + '.' + $index;
                            <div
                              [class]="
                                selectedPath() === childPath
                                  ? 'middt-ui-widget-card middt-ui-widget-card-selected'
                                  : 'middt-ui-widget-card'
                              "
                              (click)="$event.stopPropagation(); selectedPath.set(childPath)">
                              <div class="middt-ui-toolbar" style="justify-content: space-between">
                                <div style="display: flex; align-items: center; gap: .4rem">
                                  <span>{{ getWidgetIcon(child.widget) }}</span>
                                  <span class="middt-ui-badge middt-ui-badge-primary" style="font-size: .7rem">
                                    {{ child.widget }}
                                  </span>
                                  <strong style="font-size: .82rem">{{ getWidgetSummary(child) }}</strong>
                                </div>
                                <div class="middt-ui-toolbar" (click)="$event.stopPropagation()">
                                  <button
                                    mat-button
                                    type="button"
                                    (click)="deleteChildNode(node, $index)">✕</button>
                                </div>
                              </div>

                              @if (child.widget === 'fields') {
                                <div style="display: flex; flex-wrap: wrap; gap: .3rem">
                                  @for (fItem of getNodeArray(child, 'items'); track $index) {
                                    <span class="middt-wf-edge-pill" style="width: auto; font-size: .7rem; padding: .1rem .4rem">
                                      <strong>{{ fItem['label'] }}:</strong> <code>{{ fItem['value'] }}</code>
                                    </span>
                                  }
                                </div>
                              }

                              @if (isContainerWidget(child.widget)) {
                                <div
                                  class="middt-ui-widget-children"
                                  (dragover)="$event.preventDefault(); $event.stopPropagation()"
                                  (drop)="$event.stopPropagation(); onDropIntoContainer(childPath)">
                                  <div class="middt-ui-toolbar" style="justify-content: space-between">
                                    <small class="middt-ui-text-muted">
                                      İç Bileşenler ({{ (child.children ?? []).length }})
                                    </small>
                                    <select
                                      class="middt-wf-input"
                                      style="width: 150px; padding: .1rem .3rem; font-size: .72rem"
                                      value=""
                                      (change)="addChildWidget(child, childPath, $any($event.target).value); $any($event.target).value = ''">
                                      <option value="">+ Bileşen Ekle...</option>
                                      @for (p of paletteItems; track p.widget) {
                                        <option [value]="p.widget">{{ p.icon }} {{ p.title }}</option>
                                      }
                                    </select>
                                  </div>
                                  @for (grandChild of child.children ?? []; track $index) {
                                    @let grandPath = childPath + '.' + $index;
                                    <div
                                      [class]="
                                        selectedPath() === grandPath
                                          ? 'middt-ui-widget-card middt-ui-widget-card-selected'
                                          : 'middt-ui-widget-card'
                                      "
                                      (click)="$event.stopPropagation(); selectedPath.set(grandPath)">
                                      <div class="middt-ui-toolbar" style="justify-content: space-between">
                                        <span>
                                          {{ getWidgetIcon(grandChild.widget) }}
                                          <strong>{{ grandChild.widget }}:</strong>
                                          {{ getWidgetSummary(grandChild) }}
                                        </span>
                                        <button
                                          mat-button
                                          type="button"
                                          (click)="$event.stopPropagation(); deleteChildNode(child, $index)">✕</button>
                                      </div>
                                    </div>
                                  }
                                </div>
                              }
                            </div>
                          }
                        </div>
                      }
                    </div>
                  }
                </div>
              }
            }
          </div>

          @if (preview(); as p) {
            <mat-card appearance="outlined">
              <mat-card-content>
                <div class="middt-ui-toolbar" style="justify-content: space-between; margin-bottom: .5rem">
                  <h2 class="middt-ui-section-title">👁️ Canlı Önizleme</h2>
                  <button mat-button type="button" (click)="preview.set(null)">Gizle</button>
                </div>
                @for (k of [previewKey()]; track k) {
                  <middt-ui-layout-view [layout]="p.layout" [context]="p.context" />
                }
              </mat-card-content>
            </mat-card>
          }

          <mat-card appearance="outlined">
            <mat-card-content>
              <div class="middt-ui-toolbar" style="justify-content: space-between; margin-bottom: .4rem">
                <label for="ui-layout-json" style="font-weight: 600; font-size: .85rem">
                  &#123; &#125; Yerleşim JSON Kodu (Görsel tuval ile çift yönlü senkronize)
                </label>
                @if (revisions().length > 0) {
                  <details>
                    <summary style="font-size: .8rem; cursor: pointer">Önceki sürümler ({{ revisions().length }})</summary>
                    <ul class="middt-ui-list">
                      @for (revision of revisions(); track revision.version) {
                        <li>
                          v{{ revision.version }} · {{ formatDate(revision.createdAt) }} · {{ revision.createdBy }}
                          <button mat-button type="button" (click)="loadRevision(revision)">Editöre yükle</button>
                        </li>
                      }
                    </ul>
                  </details>
                }
              </div>
              <textarea
                id="ui-layout-json"
                class="middt-ui-editor"
                style="min-height: 200px"
                spellcheck="false"
                [value]="json()"
                (change)="onRawJsonInput($any($event.target).value)"></textarea>
            </mat-card-content>
          </mat-card>
        </div>

        <!-- 3. Right Column: Context-Sensitive Property Inspector -->
        <aside class="middt-wf-inspector" aria-label="Seçili bileşen veya ekran özellikleri">
          @if (selectedNode(); as node) {
            <div class="middt-ui-form">
              <div class="middt-ui-toolbar" style="justify-content: space-between">
                <div>
                  <span class="middt-ui-badge middt-ui-badge-primary">🧩 Bileşen ({{ node.widget }})</span>
                  <h3 class="middt-ui-section-title" style="margin-top: .25rem">{{ getWidgetTitle(node.widget) }}</h3>
                </div>
                <button mat-stroked-button type="button" (click)="selectedPath.set(null)">Ekran Ayarları</button>
              </div>

              <div class="middt-wf-grid-2">
                <div class="middt-ui-input">
                  <label>Bileşen Tipi (widget)</label>
                  <select
                    class="middt-wf-input"
                    [value]="node.widget"
                    (change)="changeWidgetType(node, $any($event.target).value)">
                    @for (p of paletteItems; track p.widget) {
                      <option [value]="p.widget">{{ p.icon }} {{ p.title }}</option>
                    }
                  </select>
                </div>
                <div class="middt-ui-input">
                  <label>Eleman Kimliği (id — opsiyonel)</label>
                  <input
                    class="middt-wf-input"
                    [value]="node.id ?? ''"
                    (change)="setNodeOptionalProp(node, 'id', $any($event.target).value)" />
                </div>
              </div>

              <div class="middt-ui-input">
                <label>Görünürlük Koşulu (visibleWhen — C# İfadesi)</label>
                <input
                  class="middt-wf-input"
                  placeholder="data.amount > 50000"
                  [value]="node.visibleWhen ?? ''"
                  (change)="setNodeOptionalProp(node, 'visibleWhen', $any($event.target).value)" />
              </div>

              <hr class="middt-ui-divider" />

              @if (node.widget === 'heading') {
                <div class="middt-ui-input">
                  <label>Başlık Metni (text)</label>
                  <input
                    class="middt-wf-input"
                    [value]="getNodeString(node, 'text')"
                    (change)="setNodeString(node, 'text', $any($event.target).value)" />
                </div>
                <div class="middt-ui-input">
                  <label>Seviye (level: 1–6)</label>
                  <select
                    class="middt-wf-input"
                    [value]="node['level'] ?? 2"
                    (change)="setNodeInt(node, 'level', $any($event.target).value, 2)">
                    <option value="1">H1</option>
                    <option value="2">H2</option>
                    <option value="3">H3</option>
                    <option value="4">H4</option>
                  </select>
                </div>
              } @else if (node.widget === 'text') {
                <div class="middt-ui-input">
                  <label>Metin İçeriği (text)</label>
                  <textarea
                    class="middt-wf-input middt-wf-code-box"
                    [value]="getNodeString(node, 'text')"
                    (change)="setNodeString(node, 'text', $any($event.target).value)"></textarea>
                </div>
                <div class="middt-ui-input">
                  <label>Metin Stili (style)</label>
                  <select
                    class="middt-wf-input"
                    [value]="getNodeString(node, 'style') || 'normal'"
                    (change)="setNodeString(node, 'style', $any($event.target).value)">
                    <option value="normal">normal</option>
                    <option value="muted">muted</option>
                    <option value="lead">lead</option>
                    <option value="small">small</option>
                    <option value="code">code</option>
                  </select>
                </div>
              } @else if (node.widget === 'alert') {
                <div class="middt-ui-input">
                  <label>Uyarı Mesajı (text)</label>
                  <textarea
                    class="middt-wf-input middt-wf-code-box"
                    [value]="getNodeString(node, 'text')"
                    (change)="setNodeString(node, 'text', $any($event.target).value)"></textarea>
                </div>
                <div class="middt-ui-input">
                  <label>Uyarı Türü (severity)</label>
                  <select
                    class="middt-wf-input"
                    [value]="getNodeString(node, 'severity') || 'info'"
                    (change)="setNodeString(node, 'severity', $any($event.target).value)">
                    <option value="info">info (Bilgi)</option>
                    <option value="success">success (Başarılı)</option>
                    <option value="warning">warning (Uyarı)</option>
                    <option value="danger">danger (Tehlike)</option>
                  </select>
                </div>
              } @else if (node.widget === 'badge') {
                <div class="middt-ui-input">
                  <label>Rozet Metni (text)</label>
                  <input
                    class="middt-wf-input"
                    [value]="getNodeString(node, 'text')"
                    (change)="setNodeString(node, 'text', $any($event.target).value)" />
                </div>
                <div class="middt-ui-input">
                  <label>Rozet Rengi (color)</label>
                  <select
                    class="middt-wf-input"
                    [value]="getNodeString(node, 'color') || 'primary'"
                    (change)="setNodeString(node, 'color', $any($event.target).value)">
                    <option value="primary">primary</option>
                    <option value="secondary">secondary</option>
                    <option value="success">success</option>
                    <option value="warning">warning</option>
                    <option value="danger">danger</option>
                    <option value="info">info</option>
                  </select>
                </div>
              } @else if (node.widget === 'section') {
                <div class="middt-ui-input">
                  <label>Kart Başlığı (title)</label>
                  <input
                    class="middt-wf-input"
                    [value]="getNodeString(node, 'title')"
                    (change)="setNodeString(node, 'title', $any($event.target).value)" />
                </div>
              } @else if (node.widget === 'fields') {
                @let rows = getNodeArray(node, 'items');
                <div class="middt-ui-toolbar" style="justify-content: space-between">
                  <h4 class="middt-ui-section-title">📋 Alan Satırları ({{ rows.length }})</h4>
                  <button mat-stroked-button type="button" (click)="addFieldsRow(node)">
                    <mat-icon>add</mat-icon> Alan Ekle
                  </button>
                </div>
                @for (row of rows; track $index) {
                  <div class="middt-wf-task-card">
                    <div class="middt-ui-toolbar" style="justify-content: space-between">
                      <strong>#{{ $index + 1 }} {{ row['label'] || 'Alan' }}</strong>
                      <button mat-button color="warn" type="button" (click)="removeFieldsRow(node, $index)">Sil</button>
                    </div>
                    <div class="middt-wf-grid-2">
                      <div class="middt-ui-input">
                        <label>Etiket (label)</label>
                        <input
                          class="middt-wf-input"
                          [value]="row['label'] ?? ''"
                          (change)="setRowField(row, 'label', $any($event.target).value)" />
                      </div>
                      <div class="middt-ui-input">
                        <label>Değer Şablonu (value)</label>
                        <input
                          class="middt-wf-input"
                          placeholder="{{ '{{ data.amount }}' }}"
                          [value]="row['value'] ?? ''"
                          (change)="setRowField(row, 'value', $any($event.target).value)" />
                      </div>
                      <div class="middt-ui-input">
                        <label>Biçim (format)</label>
                        <select
                          class="middt-wf-input"
                          [value]="row['format'] ?? ''"
                          (change)="setRowField(row, 'format', $any($event.target).value)">
                          <option value="">Yok (Metin)</option>
                          <option value="N2">N2 (Para / Ondalık)</option>
                          <option value="N0">N0 (Tam Sayı)</option>
                          <option value="g">g (Tarih + Saat)</option>
                        </select>
                      </div>
                      <div class="middt-ui-input">
                        <label>Görünürlük (visibleWhen)</label>
                        <input
                          class="middt-wf-input"
                          [value]="row['visibleWhen'] ?? ''"
                          (change)="setRowField(row, 'visibleWhen', $any($event.target).value)" />
                      </div>
                    </div>
                  </div>
                }
              } @else if (node.widget === 'layout') {
                <div class="middt-ui-input">
                  <label>Gömülecek Alt Ekran Anahtarı (key)</label>
                  <select
                    class="middt-wf-input"
                    [value]="getNodeString(node, 'key')"
                    (change)="setNodeString(node, 'key', $any($event.target).value)">
                    <option value="">(Bir görünüm seçiniz)</option>
                    @for (l of layouts(); track l.key) {
                      @if (l.key !== doc().key) {
                        <option [value]="l.key">{{ l.key }} ({{ l.title ?? l.key }} · {{ l.kind }})</option>
                      }
                    }
                  </select>
                </div>
                <div class="middt-ui-input">
                  <label>Veri Alt İfadesi (data — opsiyonel)</label>
                  <input
                    class="middt-wf-input"
                    placeholder="data.customer veya data.riskResult"
                    [value]="getNodeString(node, 'data')"
                    (change)="setNodeString(node, 'data', $any($event.target).value)" />
                </div>
              } @else if (node.widget === 'list') {
                <div class="middt-ui-input">
                  <label>Dizi Kaynağı İfadesi (source)</label>
                  <input
                    class="middt-wf-input"
                    placeholder="data.payments"
                    [value]="getNodeString(node, 'source')"
                    (change)="setNodeString(node, 'source', $any($event.target).value)" />
                </div>
                <div class="middt-ui-input">
                  <label>Boş Liste Mesajı (empty)</label>
                  <input
                    class="middt-wf-input"
                    [value]="getNodeString(node, 'empty')"
                    (change)="setNodeString(node, 'empty', $any($event.target).value)" />
                </div>
              }
            </div>
          } @else {
            <!-- Root Screen / Layout Settings -->
            <div class="middt-ui-form">
              <span class="middt-ui-badge middt-ui-badge-info">🖥️ Ekran / Yerleşim Ayarları</span>
              <h3 class="middt-ui-section-title">{{ doc().title || doc().key }}</h3>

              <div class="middt-ui-input">
                <label>Ekran Anahtarı (key)</label>
                <input
                  class="middt-wf-input"
                  [disabled]="selectedKey() !== null"
                  [value]="doc().key"
                  (change)="setDocKey($any($event.target).value)" />
                <small class="middt-ui-text-muted">
                  İş akışı durumlarında <code>view.layout</code> alanına bu anahtar yazılır; sayfalarda ise
                  <code>/ui/{{ doc().key }}</code> adresini oluşturur.
                </small>
              </div>

              <div class="middt-ui-input">
                <label>Ekran Başlığı (title)</label>
                <input
                  class="middt-wf-input"
                  [value]="doc().title ?? ''"
                  (change)="setDocTitle($any($event.target).value)" />
              </div>

              <div class="middt-ui-input">
                <label>Ekran Türü (kind)</label>
                <select
                  class="middt-wf-input"
                  [value]="doc().kind ?? 'page'"
                  (change)="setDocKind($any($event.target).value)">
                  <option value="view">view (İş Akışı Durum Ekranı / Gömülü Görünüm)</option>
                  <option value="page">page (Bağımsız Sayfa & Sol Menü Öğesi)</option>
                </select>
              </div>

              @if (doc().kind === 'page') {
                <hr class="middt-ui-divider" />
                <h4 class="middt-ui-section-title">🧭 Sol Menü Ayarları (menu)</h4>
                <div class="middt-wf-grid-2">
                  <div class="middt-ui-input">
                    <label>Menü Başlığı (menu.title)</label>
                    <input
                      class="middt-wf-input"
                      [value]="doc().menu?.title ?? ''"
                      (change)="setMenuProp('title', $any($event.target).value)" />
                  </div>
                  <div class="middt-ui-input">
                    <label>Menü İkonu (menu.icon)</label>
                    <input
                      class="middt-wf-input"
                      placeholder="dashboard"
                      [value]="doc().menu?.icon ?? ''"
                      (change)="setMenuProp('icon', $any($event.target).value)" />
                  </div>
                </div>
              }
            </div>
          }
        </aside>
      </div>
    </div>
  `,
  styles: `
    .middt-ui-editor { box-sizing: border-box; margin-top: .25rem; padding: .5rem; resize: vertical; color: inherit; background: transparent;
      border: 1px solid var(--mat-sys-outline-variant, #ccc); border-radius: 4px; width: 100%; }
    .middt-ui-danger { --mat-button-filled-container-color: var(--mat-sys-error); --mat-button-filled-label-text-color: var(--mat-sys-on-error); }
  `,
})
export class MiddtLayoutEditorComponent implements OnInit {
  readonly #client = inject(UI_API_CLIENT);
  readonly #layoutCache = inject(UiLayoutCache);
  readonly #notify = inject(NotifyService);
  readonly #dialog = inject(MatDialog);
  readonly #contexts = inject(UiRenderContextFactory);
  readonly #locale = inject(MIDDT_CONFIG).locale;

  readonly embedded = input<boolean>(false);
  readonly initialKey = input<string | null>(null);
  readonly initialWorkflowKey = input<string | null>(null);
  readonly initialDocument = input<UiLayoutDocument | null>(null);
  readonly workflowFieldsInput = input<WorkflowSchemaFieldHint[]>([]);

  /** Raised after a save or delete (e.g. to refresh the menu). */
  readonly changed = output<void>();
  readonly savedKey = output<string>();
  readonly closed = output<void>();

  protected readonly paletteItems = PALETTE_ITEMS;
  protected readonly layouts = signal<UiLayoutSummary[]>([]);
  protected readonly widgets = signal<UiWidgetSpecModel[]>([]);
  protected readonly revisions = signal<UiLayoutRevision[]>([]);
  protected readonly errors = signal<string[]>([]);
  protected readonly selectedKey = signal<string | null>(null);
  protected readonly selectedPath = signal<string | null>(null);
  protected readonly paletteFilter = signal<string>('');
  protected readonly version = signal(0);
  protected readonly doc = signal<UiLayoutDocument>(createDefaultDoc('page'));
  protected readonly json = signal('');
  protected readonly validMessage = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly preview = signal<{ layout: UiLayoutDocument; context: UiRenderContext } | null>(null);
  protected readonly previewKey = signal(0);

  #dragWidgetType: string | null = null;
  #dragSourcePath: string | null = null;

  protected readonly filteredPaletteGroups = computed(() => {
    const filter = this.paletteFilter().trim().toLowerCase();
    const items = filter
      ? PALETTE_ITEMS.filter(
          p =>
            p.widget.toLowerCase().includes(filter) ||
            p.title.toLowerCase().includes(filter) ||
            p.description.toLowerCase().includes(filter),
        )
      : PALETTE_ITEMS;
    const groups = new Map<string, WidgetPaletteItem[]>();
    for (const item of items) {
      const arr = groups.get(item.category) ?? [];
      arr.push(item);
      groups.set(item.category, arr);
    }

    return Array.from(groups.entries()).map(([category, groupItems]) => ({ category, items: groupItems }));
  });

  protected readonly selectedNode = computed<UiNode | null>(() => {
    const path = this.selectedPath();
    if (!path) {
      return null;
    }

    return this.#findNodeByPath(path);
  });

  async ngOnInit(): Promise<void> {
    this.#syncJsonFromDoc();
    await this.#loadList();
    const response = await this.#client.widgets();
    this.widgets.set(response.data ?? []);

    let targetKey = this.initialKey();
    if (!this.embedded() && !targetKey && typeof window !== 'undefined') {
      const params = new URLSearchParams(window.location.search);
      targetKey = params.get('key');
    }

    const initDoc = this.initialDocument();
    if (initDoc) {
      const exists = this.layouts().some(l => l.key === initDoc.key);
      this.selectedKey.set(exists ? initDoc.key : null);
      this.version.set(initDoc.version ?? 0);
      this.doc.set(structuredClone(initDoc));
      this.selectedPath.set(null);
      this.#syncJsonFromDoc();
    } else if (targetKey) {
      if (this.layouts().some(l => l.key === targetKey)) {
        await this.open(targetKey);
      } else {
        this.selectedKey.set(null);
        this.version.set(0);
        this.doc.set(createDefaultDoc('view', targetKey));
        if (this.workflowFieldsInput().length > 0) {
          this.generateSectionFromWorkflowSchema();
        } else {
          this.#syncJsonFromDoc();
        }
      }
    }
  }

  protected async onSelectLayout(key: string): Promise<void> {
    if (!key) {
      this.newLayout('view');
      return;
    }

    await this.open(key);
  }

  protected newLayout(kind: 'page' | 'view' = 'page'): void {
    this.selectedKey.set(null);
    this.selectedPath.set(null);
    this.version.set(0);
    this.doc.set(createDefaultDoc(kind));
    this.revisions.set([]);
    this.#syncJsonFromDoc();
    this.#resetMessages();
  }

  protected async open(key: string): Promise<void> {
    const response = await this.#client.get(key);
    if (!isSuccess(response) || !response.data) {
      this.#notify.error('Yüklenemedi', errorText(response));
      return;
    }

    this.selectedKey.set(key);
    this.selectedPath.set(null);
    this.version.set(response.data.version ?? 0);
    this.doc.set(structuredClone(response.data));
    this.#syncJsonFromDoc();
    this.#resetMessages();
    const history = await this.#client.revisions(key);
    this.revisions.set(history.data ?? []);
  }

  protected loadRevision(revision: UiLayoutRevision): void {
    try {
      const document = parseLayout(revision.content);
      document.version = this.version();
      this.doc.set(document);
      this.selectedPath.set(null);
      this.#syncJsonFromDoc();
      this.#resetMessages();
    } catch (error) {
      this.errors.set([error instanceof Error ? error.message : String(error)]);
    }
  }

  protected format(): void {
    const document = this.#tryParse();
    if (document !== null) {
      this.doc.set(document);
      this.#syncJsonFromDoc();
    }
  }

  protected onRawJsonInput(raw: string): void {
    this.json.set(raw);
    const parsed = this.#tryParse();
    if (parsed !== null) {
      this.doc.set(parsed);
    }
  }

  protected async validate(): Promise<void> {
    const document = this.#tryParse();
    if (document === null) {
      return;
    }

    this.doc.set(document);
    const response = await this.#client.validate(document);
    if (!isSuccess(response)) {
      this.errors.set([...response.messageList]);
      return;
    }

    const errors = response.data?.errors ?? [];
    this.errors.set(errors);
    this.validMessage.set(errors.length === 0 ? '✔ Ekran yerleşimi ve tüm ifadeler geçerli.' : null);
  }

  protected async previewDraft(): Promise<void> {
    const document = this.#tryParse();
    if (document === null) {
      return;
    }

    this.doc.set(document);
    await this.#contexts.ready();
    const sampleData: Record<string, unknown> = {
      title: 'Örnek Kayıt Başlığı',
      customerName: 'Anadolu Lojistik A.Ş.',
      customerKey: 'CUST-2026-001',
      amount: 250000,
      termMonths: 24,
      status: 'Active',
    };
    for (const f of this.workflowFieldsInput()) {
      if (!(f.field in sampleData)) {
        sampleData[f.field] = f.type === 'number' || f.type === 'integer' ? 1000 : `Örnek ${f.title}`;
      }
    }

    this.preview.set({
      layout: document,
      context: this.#contexts.create(
        {
          [UiExpressions.query]: {},
          data: sampleData,
          instance: { key: 'WF-PREVIEW-01', state: 'review', status: 'Active' },
        },
        'ui-preview',
      ),
    });
    this.previewKey.update(k => k + 1);
    const response = await this.#client.validate(document);
    this.errors.set(isSuccess(response) ? (response.data?.errors ?? []) : [...response.messageList]);
  }

  protected async save(): Promise<void> {
    const document = this.#tryParse();
    if (document === null) {
      return;
    }

    const selectedKey = this.selectedKey();
    if (selectedKey !== null && document.key !== selectedKey) {
      this.errors.set(["Kayıtlı bir yerleşimin anahtarı değiştirilemez; yeni yerleşim için 'Yeni' kullanın."]);
      return;
    }

    document.version = this.version();
    this.busy.set(true);
    try {
      const response = await this.#client.save(document);
      if (!isSuccess(response) || !response.data) {
        this.errors.set([...response.messageList]);
        return;
      }

      const saved = response.data;
      this.#layoutCache.invalidate(saved.key);
      this.selectedKey.set(saved.key);
      this.version.set(saved.version ?? 0);
      this.doc.set(structuredClone(saved));
      this.#syncJsonFromDoc();
      this.validMessage.set(`✔ '${saved.key}' ekran yerleşimi kaydedildi (v${saved.version ?? 0}).`);
      await this.#loadList();
      this.revisions.set((await this.#client.revisions(saved.key)).data ?? []);
      this.savedKey.emit(saved.key);
      this.changed.emit();
    } finally {
      this.busy.set(false);
    }
  }

  protected async remove(): Promise<void> {
    const key = this.selectedKey();
    if (
      key === null ||
      !(await confirmDialog(this.#dialog, {
        title: 'Sil',
        message: `'${key}' silinsin mi?`,
        ok: 'Sil',
        cancel: 'Vazgeç',
      }))
    ) {
      return;
    }

    const response = await this.#client.delete(key, this.version());
    if (!isSuccess(response)) {
      this.errors.set([...response.messageList]);
      return;
    }

    this.#layoutCache.invalidate(key);
    this.#notify.success('Silindi', key);
    this.newLayout('page');
    await this.#loadList();
    this.changed.emit();
  }

  // --- Drag & Drop and Visual Tree Helpers ---

  protected startPaletteWidgetDrag(widget: string): void {
    this.#dragWidgetType = widget;
    this.#dragSourcePath = null;
  }

  protected startExistingNodeDrag(path: string): void {
    this.#dragSourcePath = path;
    this.#dragWidgetType = null;
  }

  protected onDropIntoContainer(containerPath: string): void {
    const d = this.doc();
    d.body ??= [];
    let targetList = d.body;

    if (containerPath) {
      const container = this.#findNodeByPath(containerPath);
      if (container && this.isContainerWidget(container.widget)) {
        container.children ??= [];
        targetList = container.children;
      }
    }

    if (this.#dragWidgetType) {
      targetList.push(createWidgetNode(this.#dragWidgetType));
      const newIdx = targetList.length - 1;
      this.selectedPath.set(containerPath ? `${containerPath}.${newIdx}` : `${newIdx}`);
      this.#touchDoc();
    } else if (
      this.#dragSourcePath &&
      this.#dragSourcePath !== containerPath &&
      !containerPath.startsWith(`${this.#dragSourcePath}.`)
    ) {
      const moved = this.#removeNodeAtPath(this.#dragSourcePath);
      if (moved) {
        targetList.push(moved);
        this.selectedPath.set(null);
        this.#touchDoc();
      }
    }

    this.#dragWidgetType = null;
    this.#dragSourcePath = null;
  }

  protected quickAddWidget(widget: string): void {
    const d = this.doc();
    d.body ??= [];
    const sel = this.selectedNode();
    const selPath = this.selectedPath();
    if (sel && selPath && this.isContainerWidget(sel.widget)) {
      sel.children ??= [];
      sel.children.push(createWidgetNode(widget));
      this.selectedPath.set(`${selPath}.${sel.children.length - 1}`);
      this.#touchDoc();
      return;
    }

    d.body.push(createWidgetNode(widget));
    this.selectedPath.set(`${d.body.length - 1}`);
    this.#touchDoc();
  }

  protected addChildWidget(container: UiNode, containerPath: string, widget: string): void {
    if (!widget) {
      return;
    }

    container.children ??= [];
    container.children.push(createWidgetNode(widget));
    this.selectedPath.set(`${containerPath}.${container.children.length - 1}`);
    this.#touchDoc();
  }

  protected moveRootNode(index: number, delta: number): void {
    const body = this.doc().body ?? [];
    const next = index + delta;
    if (next < 0 || next >= body.length) {
      return;
    }

    const tmp = body[index]!;
    body[index] = body[next]!;
    body[next] = tmp;
    this.#touchDoc();
  }

  protected duplicateRootNode(index: number): void {
    const body = this.doc().body ?? [];
    const orig = body[index];
    if (!orig) {
      return;
    }

    body.splice(index + 1, 0, structuredClone(orig));
    this.#touchDoc();
  }

  protected deleteRootNode(index: number): void {
    const body = this.doc().body ?? [];
    body.splice(index, 1);
    this.selectedPath.set(null);
    this.#touchDoc();
  }

  protected deleteChildNode(parent: UiNode, index: number): void {
    parent.children?.splice(index, 1);
    this.selectedPath.set(null);
    this.#touchDoc();
  }

  protected generateSectionFromWorkflowSchema(): void {
    const fields = this.workflowFieldsInput();
    if (fields.length === 0) {
      return;
    }

    const items = fields.map(f => ({
      label: f.title,
      value: `{{ data.${f.field} }}`,
      visibleWhen: `data.${f.field} != null`,
      ...(f.type === 'number' ? { format: 'N2' } : {}),
    }));

    const d = this.doc();
    d.body ??= [];
    d.body.push({
      widget: 'section',
      title: `${d.title ?? d.key} — Kayıt Bilgileri`,
      children: [
        {
          widget: 'fields',
          items,
        },
      ],
    });
    this.selectedPath.set(`${d.body.length - 1}.0`);
    this.#touchDoc();
  }

  protected insertWorkflowField(f: WorkflowSchemaFieldHint): void {
    const row: Record<string, unknown> = {
      label: f.title,
      value: `{{ data.${f.field} }}`,
      visibleWhen: `data.${f.field} != null`,
    };
    if (f.type === 'number') {
      row['format'] = 'N2';
    }

    const sel = this.selectedNode();
    if (sel && sel.widget === 'fields') {
      const items = this.getNodeArray(sel, 'items');
      items.push(row);
      sel['items'] = items;
      this.#touchDoc();
      return;
    }

    const d = this.doc();
    d.body ??= [];
    d.body.push({
      widget: 'fields',
      items: [row],
    });
    this.selectedPath.set(`${d.body.length - 1}`);
    this.#touchDoc();
  }

  // --- Inspector Setters ---

  protected setDocKey(val: string): void {
    this.doc().key = val.trim() || 'new-layout';
    this.#touchDoc();
  }

  protected setDocTitle(val: string): void {
    const trimmed = val.trim();
    if (trimmed) {
      this.doc().title = trimmed;
    } else {
      delete this.doc().title;
    }
    this.#touchDoc();
  }

  protected setDocKind(kind: string): void {
    const d = this.doc();
    d.kind = kind === 'view' ? 'view' : 'page';
    if (d.kind === 'view') {
      delete d.menu;
    } else {
      d.menu ??= { title: d.title ?? d.key, icon: 'dashboard', order: 100 };
    }
    this.#touchDoc();
  }

  protected setMenuProp(prop: keyof UiMenu, val: string): void {
    const d = this.doc();
    d.menu ??= { title: d.title ?? d.key, icon: 'dashboard', order: 100 };
    (d.menu as Record<string, unknown>)[prop] = val.trim();
    this.#touchDoc();
  }

  protected changeWidgetType(node: UiNode, newWidget: string): void {
    const tpl = createWidgetNode(newWidget);
    for (const k of Object.keys(node)) {
      if (k !== 'id' && k !== 'visibleWhen') {
        delete node[k];
      }
    }
    Object.assign(node, tpl);
    this.#touchDoc();
  }

  protected setNodeOptionalProp(node: UiNode, prop: 'id' | 'visibleWhen', val: string): void {
    const trimmed = val.trim();
    if (trimmed) {
      node[prop] = trimmed;
    } else {
      delete node[prop];
    }
    this.#touchDoc();
  }

  protected getNodeString(node: UiNode, prop: string): string {
    const v = node[prop];
    return typeof v === 'string' ? v : '';
  }

  protected setNodeString(node: UiNode, prop: string, val: string): void {
    const trimmed = val.trim();
    if (trimmed) {
      node[prop] = trimmed;
    } else {
      delete node[prop];
    }
    this.#touchDoc();
  }

  protected setNodeInt(node: UiNode, prop: string, raw: string, fallback: number): void {
    const parsed = Number.parseInt(raw, 10);
    node[prop] = Number.isFinite(parsed) ? parsed : fallback;
    this.#touchDoc();
  }

  protected getNodeArray(node: UiNode, prop: string): Record<string, unknown>[] {
    const v = node[prop];
    return Array.isArray(v) ? (v as Record<string, unknown>[]) : [];
  }

  protected addFieldsRow(node: UiNode): void {
    const rows = this.getNodeArray(node, 'items');
    rows.push({ label: 'Yeni Alan', value: '{{ data.title }}' });
    node['items'] = rows;
    this.#touchDoc();
  }

  protected removeFieldsRow(node: UiNode, idx: number): void {
    const rows = this.getNodeArray(node, 'items');
    rows.splice(idx, 1);
    node['items'] = rows;
    this.#touchDoc();
  }

  protected setRowField(row: Record<string, unknown>, prop: string, val: string): void {
    const trimmed = val.trim();
    if (trimmed) {
      row[prop] = trimmed;
    } else {
      delete row[prop];
    }
    this.#touchDoc();
  }

  protected isContainerWidget(widget: string): boolean {
    return widget === 'section' || widget === 'columns' || widget === 'list' || widget === 'record';
  }

  protected getWidgetIcon(widget: string): string {
    return PALETTE_ITEMS.find(p => p.widget === widget)?.icon ?? '🧩';
  }

  protected getWidgetTitle(widget: string): string {
    return PALETTE_ITEMS.find(p => p.widget === widget)?.title ?? widget;
  }

  protected getWidgetSummary(node: UiNode): string {
    switch (node.widget) {
      case 'section':
        return this.getNodeString(node, 'title') || 'Bölüm Kartı';
      case 'columns':
        return `Yan Yana Sütunlar (${node.children?.length ?? 0} sütun)`;
      case 'fields':
        return `${this.getNodeArray(node, 'items').length} alan satırı`;
      case 'heading':
      case 'text':
        return this.getNodeString(node, 'text') || node.widget;
      case 'alert':
        return `[${this.getNodeString(node, 'severity') || 'info'}] ${this.getNodeString(node, 'text')}`;
      case 'badge':
        return `[${this.getNodeString(node, 'color') || 'primary'}] ${this.getNodeString(node, 'text')}`;
      case 'layout':
        return `Alt Ekran: ${this.getNodeString(node, 'key') || '(seçilmedi)'}`;
      case 'list':
        return `Dizi: ${this.getNodeString(node, 'source') || 'data.items'}`;
      case 'grid':
        return `Tablo: ${this.getNodeString(node, 'entity')}`;
      default:
        return node.widget;
    }
  }

  protected formatDate(value: string): string {
    const date = new Date(value);
    return Number.isNaN(date.getTime())
      ? value
      : new Intl.DateTimeFormat(this.#locale, {
          year: 'numeric',
          month: '2-digit',
          day: '2-digit',
          hour: 'numeric',
          minute: '2-digit',
        }).format(date);
  }

  #findNodeByPath(path: string): UiNode | null {
    const parts = path.split('.').filter(Boolean);
    let list: UiNode[] | null | undefined = this.doc().body;
    let current: UiNode | null = null;
    for (const part of parts) {
      const idx = Number.parseInt(part, 10);
      if (!list || !Number.isFinite(idx) || idx < 0 || idx >= list.length) {
        return null;
      }
      current = list[idx]!;
      list = current.children;
    }
    return current;
  }

  #removeNodeAtPath(path: string): UiNode | null {
    const parts = path.split('.').filter(Boolean);
    if (parts.length === 0) {
      return null;
    }
    let list: UiNode[] | null | undefined = this.doc().body;
    for (let i = 0; i < parts.length - 1; i++) {
      const idx = Number.parseInt(parts[i]!, 10);
      if (!list || idx < 0 || idx >= list.length) {
        return null;
      }
      list = list[idx]!.children;
    }
    const lastIdx = Number.parseInt(parts[parts.length - 1]!, 10);
    if (!list || lastIdx < 0 || lastIdx >= list.length) {
      return null;
    }
    const [removed] = list.splice(lastIdx, 1);
    return removed ?? null;
  }

  async #loadList(): Promise<void> {
    const response = await this.#client.list();
    if (isSuccess(response)) {
      this.layouts.set(response.data ?? []);
    } else {
      this.#notify.error('Yerleşimler yüklenemedi', errorText(response));
    }
  }

  #touchDoc(): void {
    this.doc.set({ ...this.doc() });
    this.#syncJsonFromDoc();
  }

  #syncJsonFromDoc(): void {
    this.json.set(serializeLayout(this.doc()));
    this.validMessage.set(null);
  }

  #tryParse(): UiLayoutDocument | null {
    this.#resetMessages();
    try {
      return parseLayout(this.json());
    } catch (error) {
      this.errors.set([`Geçersiz JSON: ${error instanceof Error ? error.message : String(error)}`]);
      return null;
    }
  }

  #resetMessages(): void {
    this.errors.set([]);
    this.validMessage.set(null);
  }
}

function createDefaultDoc(kind: 'page' | 'view', customKey?: string): UiLayoutDocument {
  const isView = kind === 'view';
  const key = customKey ?? (isView ? 'new-workflow-view' : 'new-page');
  return {
    key,
    title: isView ? 'Yeni İş Akışı Ekranı' : 'Yeni Sayfa',
    kind,
    ...(isView ? {} : { menu: { title: 'Yeni Sayfa', icon: 'dashboard', order: 100 } }),
    body: isView
      ? [
          {
            widget: 'section',
            title: 'Kayıt Özeti',
            children: [
              {
                widget: 'fields',
                items: [
                  { label: 'Başlık', value: '{{ data.title }}' },
                  { label: 'Tutar', value: '{{ data.amount }}', format: 'N2' },
                ],
              },
            ],
          },
          { widget: 'layout', key: 'workflow-footer' },
        ]
      : [
          { widget: 'heading', text: 'Merhaba {{ user.Name }}' },
          { widget: 'text', text: 'Bu sayfa çalışma zamanında eklendi.' },
        ],
  };
}

function createWidgetNode(widget: string): UiNode {
  switch (widget) {
    case 'section':
      return {
        widget: 'section',
        title: 'Yeni Bölüm',
        children: [
          {
            widget: 'fields',
            items: [{ label: 'Başlık', value: '{{ data.title }}' }],
          },
        ],
      };
    case 'columns':
      return {
        widget: 'columns',
        widths: ['1', '1'],
        children: [
          { widget: 'section', title: 'Sol Sütun', children: [{ widget: 'text', text: 'Sol sütun içeriği' }] },
          { widget: 'section', title: 'Sağ Sütun', children: [{ widget: 'text', text: 'Sağ sütun içeriği' }] },
        ],
      };
    case 'fields':
      return {
        widget: 'fields',
        items: [
          { label: 'Başlık', value: '{{ data.title }}' },
          { label: 'Tutar', value: '{{ data.amount }}', format: 'N2' },
        ],
      };
    case 'heading':
      return { widget: 'heading', text: 'Bölüm Başlığı', level: 2 };
    case 'text':
      return { widget: 'text', text: 'Açıklama metni...', style: 'normal' };
    case 'alert':
      return { widget: 'alert', severity: 'info', text: 'Önemli bilgilendirme mesajı.' };
    case 'badge':
      return { widget: 'badge', color: 'info', text: '{{ instance.state }}' };
    case 'divider':
      return { widget: 'divider' };
    case 'link':
      return { widget: 'link', text: 'Detaya Git', href: '/ui/home', style: 'button' };
    case 'list':
      return {
        widget: 'list',
        source: 'data.items',
        empty: 'Kayıt bulunamadı.',
        children: [{ widget: 'fields', items: [{ label: 'Kalem', value: '{{ row.title }}' }] }],
      };
    case 'record':
      return {
        widget: 'record',
        entity: 'CustomerOnboarding',
        recordId: '{{ query.id }}',
        children: [{ widget: 'text', text: '{{ record.fullName }}' }],
      };
    case 'layout':
      return { widget: 'layout', key: 'workflow-footer' };
    case 'grid':
      return {
        widget: 'grid',
        entity: 'CustomerOnboarding',
        title: 'Kayıt Listesi',
        columns: [
          { field: 'key', title: 'Referans' },
          { field: 'state', title: 'Durum' },
        ],
      };
    default:
      return { widget };
  }
}
