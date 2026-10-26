import {
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  inject,
  OnInit,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { Router } from '@angular/router';
import { UI_API_CLIENT } from '@middt/angular/core';
import { MiddtLayoutEditorComponent, type WorkflowSchemaFieldHint } from '@middt/angular/ui';
import {
  isSuccess,
  type UiLayoutDocument,
  type UiLayoutSummary,
  type WorkflowData,
  type WorkflowSummaryModel,
  type WorkflowTaskSpecModel,
} from '@middt/ui-core';
import { WORKFLOW_API_CLIENT } from './workflow-client';

const START_NODE_KEY = '__start__';
const START_NODE_WIDTH = 230;
const STATE_NODE_WIDTH = 264;
const NODE_APPROX_HEIGHT = 165;
const PORT_OFFSET_Y = 30;

interface StatePaletteTemplate {
  readonly icon: string;
  readonly label: string;
  readonly type: string;
  readonly subType: string;
  readonly defaultTitle: string;
}

const STATE_TEMPLATES: readonly StatePaletteTemplate[] = [
  { icon: '👤', label: 'İnsan Görevi Durumu', type: 'Intermediate', subType: 'Human', defaultTitle: 'Uzman İncelemesi' },
  { icon: '⚙️', label: 'Otomatik Sistem Durumu', type: 'Intermediate', subType: 'None', defaultTitle: 'Sistem Kontrolü' },
  { icon: '🔀', label: 'Alt İş Akışı Durumu', type: 'SubFlow', subType: 'None', defaultTitle: 'Alt Akış Bekleme' },
  { icon: '✅', label: 'Başarılı Bitiş Durumu', type: 'Finish', subType: 'Success', defaultTitle: 'Onaylandı' },
  { icon: '❌', label: 'Red / Hata Bitişi', type: 'Finish', subType: 'Error', defaultTitle: 'Reddedildi' },
  { icon: '🚫', label: 'İptal Bitiş Durumu', type: 'Finish', subType: 'Cancelled', defaultTitle: 'İptal Edildi' },
];

interface NodePoint {
  readonly x: number;
  readonly y: number;
}

interface EdgeGeometry {
  readonly path: string;
  readonly midX: number;
  readonly midY: number;
}

interface CanvasEdge {
  readonly edgeId: string;
  readonly fromKey: string;
  readonly targetKey: string;
  readonly transitionIndex: number;
  readonly key: string;
  readonly label: string;
  readonly trigger: string;
  readonly taskCount: number;
  readonly geometry: EdgeGeometry;
}

/**
 * 2D Drag-and-Drop Visual Workflow & Task Studio (`<middt-workflow-designer />`).
 */
@Component({
  selector: 'middt-workflow-designer',
  imports: [MatCardModule, MatButtonModule, MatIconModule, MatSnackBarModule, MiddtLayoutEditorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="middt-wf-studio" data-testid="workflow-designer">
      <mat-card appearance="outlined">
        <mat-card-content>
          <div class="middt-ui-toolbar" style="justify-content: space-between">
            <div class="middt-ui-toolbar">
              <label for="wf-select" style="font-weight: 600; font-size: .88rem">İş Akışı:</label>
              <select
                id="wf-select"
                class="middt-wf-input"
                style="width: 240px"
                [value]="selectedWorkflowKey() ?? ''"
                (change)="onSelectWorkflow($any($event.target).value)">
                @if (selectedWorkflowKey() === null) {
                  <option value="">-- Yeni İş Akışı --</option>
                }
                @for (wf of workflows(); track wf.key) {
                  <option [value]="wf.key">{{ wf.title ?? wf.key }} ({{ wf.key }} · v{{ wf.version }})</option>
                }
              </select>
              <button mat-stroked-button type="button" (click)="newWorkflow()">
                <mat-icon>add</mat-icon> Yeni Akış
              </button>
              <span class="middt-ui-badge middt-ui-badge-info">v{{ getInt(doc(), 'version', 1) }}</span>
            </div>

            <div class="middt-wf-tabs" style="margin: 0; border: 0; padding: 0" role="tablist">
              <button
                type="button"
                [class]="activeTab() === 'visual' ? 'middt-wf-tab middt-wf-tab-active' : 'middt-wf-tab'"
                (click)="switchTab('visual')">
                🎨 Sürükle-Bırak Tuval
              </button>
              <button
                type="button"
                [class]="activeTab() === 'schema' ? 'middt-wf-tab middt-wf-tab-active' : 'middt-wf-tab'"
                (click)="switchTab('schema')">
                🗂️ Veri Şeması (dataSchema)
              </button>
              <button
                type="button"
                [class]="activeTab() === 'json' ? 'middt-wf-tab middt-wf-tab-active' : 'middt-wf-tab'"
                (click)="switchTab('json')">
                &#123; &#125; JSON Kodu
              </button>
              <button
                type="button"
                class="middt-wf-tab"
                title="Yerleşim ve Ekran Tasarımcısını Aç"
                (click)="openScreenModal(null, null)">
                🖥️ Ekran Tasarımcısı
              </button>
            </div>

            <div class="middt-ui-toolbar">
              @if (activeTab() === 'visual') {
                <button mat-stroked-button type="button" (click)="selectAllOnBoard()">
                  <mat-icon>select_all</mat-icon> Tümünü Seç
                </button>
                <button mat-stroked-button type="button" (click)="autoLayoutBoard()">
                  <mat-icon>auto_fix_high</mat-icon> Otomatik Hizala
                </button>
                <button mat-stroked-button type="button" (click)="changeZoom(-0.1)">-</button>
                <span style="font-size: .8rem; font-weight: 600; min-width: 42px; text-align: center">
                  {{ Math.round(zoom() * 100) }}%
                </span>
                <button mat-stroked-button type="button" (click)="changeZoom(0.1)">+</button>
              }
              <button mat-stroked-button type="button" (click)="validate()">
                <mat-icon>rule</mat-icon> Doğrula
              </button>
              <button mat-flat-button color="primary" type="button" [disabled]="busy()" (click)="save()">
                <mat-icon>save</mat-icon> Kaydet
              </button>
            </div>
          </div>

          @if (errors().length > 0) {
            <div class="middt-ui-alert middt-ui-alert-danger" style="margin-top: .75rem" role="alert">
              <strong>{{ errors().length }} doğrulama hatası:</strong>
              <ul class="middt-ui-list">
                @for (err of errors(); track err) {
                  <li>{{ err }}</li>
                }
              </ul>
            </div>
          } @else if (validMessage(); as msg) {
            <div class="middt-ui-alert middt-ui-alert-success" style="margin-top: .75rem" role="status">{{ msg }}</div>
          }

          @if (pointerMode() === 'connect-wire' && pointerSourceNodeKey(); as fromNode) {
            <div
              class="middt-ui-alert middt-ui-alert-info"
              style="margin-top: .75rem; display: flex; justify-content: space-between; align-items: center">
              <span>
                🔗 <strong>{{ fromNode === startNodeKey ? 'Başlangıç (start)' : fromNode }}</strong> düğümünden geçiş
                oku çiziliyor — fareyi bağlamak istediğiniz hedef durumun üzerine bırakın.
              </span>
              <button mat-stroked-button type="button" (click)="cancelPointerAction()">İptal</button>
            </div>
          }
        </mat-card-content>
      </mat-card>

      @if (activeTab() === 'visual') {
        <div [class]="getStudioBodyClass()">
          <!-- 1. Left Drag & Drop Palette -->
          @if (showPalette()) {
            <aside class="middt-wf-palette" aria-label="Sürükle bırak bileşen paleti">
              <div class="middt-wf-palette-group">
                <div class="middt-wf-palette-group-title">📍 Durumlar (Tuvale Sürükle)</div>
                @for (stTpl of stateTemplates; track stTpl.label) {
                  <div
                    class="middt-wf-palette-chip"
                    draggable="true"
                    title="Tuvale sürükleyip bırakın veya çift tıklayın"
                    (dragstart)="startPaletteStateDrag($event, stTpl.type, stTpl.subType, stTpl.defaultTitle)"
                    (dblclick)="addStateAtDefaultPos(stTpl.type, stTpl.subType, stTpl.defaultTitle)">
                    <span class="middt-wf-palette-chip-icon">{{ stTpl.icon }}</span>
                    <div class="middt-wf-palette-chip-text">
                      <strong>{{ stTpl.label }}</strong>
                      <small>{{ stTpl.type }} / {{ stTpl.subType }}</small>
                    </div>
                  </div>
                }
              </div>

              <hr class="middt-ui-divider" />

              <div class="middt-wf-palette-group">
                <div class="middt-wf-palette-group-title">⚡ Görevler (Durum / Geçişe Sürükle)</div>
                <input
                  class="middt-wf-input"
                  placeholder="Görev ara (set, http, agent...)"
                  [value]="paletteFilter()"
                  (input)="paletteFilter.set($any($event.target).value)" />
                @for (grp of filteredTaskGroups(); track grp.category) {
                  <div class="middt-wf-palette-group-title">{{ grp.category }}</div>
                  @for (spec of grp.items; track spec.name) {
                    <div
                      class="middt-wf-palette-chip"
                      draggable="true"
                      [title]="spec.description + ' — Bir durumun veya geçişin üzerine sürükleyin'"
                      (dragstart)="startPaletteTaskDrag($event, spec.name)"
                      (dblclick)="quickAddTaskToSelection(spec.name)">
                      <span class="middt-wf-palette-chip-icon">⚡</span>
                      <div class="middt-wf-palette-chip-text">
                        <strong>{{ spec.title }}</strong>
                        <small>{{ spec.name }}</small>
                      </div>
                    </div>
                  }
                }
              </div>
            </aside>
          }

          <!-- 2. Center 2D Interactive SVG + Nodes Board -->
          <div
            [class]="getViewportClass()"
            tabindex="0"
            aria-label="2B İş Akışı Çizim Tuvali"
            [style.background-position]="panX() + 'px ' + panY() + 'px'"
            (pointerdown)="onViewportPointerDown($event)"
            (pointermove)="onBoardPointerMove($event)"
            (pointerup)="onBoardPointerUp($event)"
            (keydown)="onCanvasKeyDown($event)">
            <!-- Floating Tool Mode Dock (Pointer/Select vs Hand/Pan + Panel Toggles) -->
            <div class="middt-wf-tool-dock" (pointerdown)="$event.stopPropagation()">
              <button
                type="button"
                class="middt-wf-tool-btn"
                [title]="showPalette() ? 'Sol bileşen paletini gizle (Tuvali genişlet)' : 'Sol bileşen paletini göster'"
                (click)="showPalette.set(!showPalette())">
                {{ showPalette() ? '◀ Palet' : '▶ Palet' }}
              </button>
              <button
                type="button"
                [class]="toolMode() === 'select' ? 'middt-wf-tool-btn middt-wf-tool-btn-active' : 'middt-wf-tool-btn'"
                title="İmleç / Seçim Modu: Nesnelere odaklanın, boş alanda basılı tutup kutu çizerek çoklu seçin veya nesneleri taşıyın"
                (click)="toolMode.set('select')">
                🖱️ Seç / Odakla
              </button>
              <button
                type="button"
                [class]="toolMode() === 'pan' ? 'middt-wf-tool-btn middt-wf-tool-btn-active' : 'middt-wf-tool-btn'"
                title="El Aracı (Kaydırma Modu): Sol tuşa basılı tutarak çizim alanını sola, sağa, yukarı ve aşağı kaydırın"
                (click)="toolMode.set('pan')">
                ✋ El Aracı (Kaydır)
              </button>
              <button
                type="button"
                class="middt-wf-tool-btn"
                title="Görünümü ve yakınlaştırmayı merkeze sıfırla"
                (click)="resetViewport()">
                🎯 Merkeze Al
              </button>
              <button
                type="button"
                class="middt-wf-tool-btn"
                [title]="showInspector() ? 'Sağ özellikler panelini gizle (Tuvali genişlet)' : 'Sağ özellikler panelini göster'"
                (click)="showInspector.set(!showInspector())">
                {{ showInspector() ? 'Özellikler ▶' : '◀ Özellikler' }}
              </button>
            </div>

            <!-- Floating Multi-Selection & Group Action Bar -->
            @if (selectedNodeKeys().size > 0 || selectedEdgeIds().size > 0) {
              <div class="middt-wf-selection-bar" (pointerdown)="$event.stopPropagation()">
                <span>
                  📦
                  @if (selectedNodeKeys().size > 0) {
                    <strong>{{ selectedNodeKeys().size }} Durum</strong>
                  }
                  @if (selectedNodeKeys().size > 0 && selectedEdgeIds().size > 0) {
                    <span> · </span>
                  }
                  @if (selectedEdgeIds().size > 0) {
                    <strong>{{ selectedEdgeIds().size }} Ok</strong>
                  }
                  seçili
                </span>

                @if (selectedNodeKeys().size > 1) {
                  <button
                    type="button"
                    class="middt-wf-selection-btn"
                    title="Seçili durumları yatay hizala"
                    (click)="alignSelectedHorizontal()">
                    📐 Yatay Hizala
                  </button>
                  <button
                    type="button"
                    class="middt-wf-selection-btn"
                    title="Seçili durumları dikey hizala"
                    (click)="alignSelectedVertical()">
                    📐 Dikey Hizala
                  </button>
                }
                @if (selectedDeletableNodeCount() > 0) {
                  <button
                    type="button"
                    class="middt-wf-selection-btn"
                    title="Seçili durumları çoğalt"
                    (click)="duplicateSelectedStates()">
                    📑 Çoğalt
                  </button>
                }
                <button
                  type="button"
                  class="middt-wf-selection-btn middt-wf-selection-btn-danger"
                  title="Seçili tüm durumları ve okları sil (Delete)"
                  (click)="deleteSelection()">
                  🗑️ Seçilenleri Sil ({{ selectedDeletableNodeCount() + selectedEdgeIds().size }})
                </button>
                <button
                  type="button"
                  class="middt-wf-selection-btn"
                  title="Seçimi temizle (Esc)"
                  (click)="clearSelection()">
                  ✕ Temizle
                </button>
              </div>
            }

            <div
              #boardEl
              class="middt-wf-board"
              [style.transform]="'translate(' + panX() + 'px, ' + panY() + 'px) scale(' + zoom() + ')'"
              (pointerdown)="$event.stopPropagation(); onBoardPointerDown($event)"
              (dragover)="$event.preventDefault()"
              (drop)="onBoardDrop($event)">
              <!-- Rubber-Band / Marquee Box Selection Overlay -->
              @if (pointerMode() === 'lasso') {
                <div
                  class="middt-wf-lasso"
                  [style.left.px]="Math.min(lassoStartX(), lassoCurrentX())"
                  [style.top.px]="Math.min(lassoStartY(), lassoCurrentY())"
                  [style.width.px]="Math.abs(lassoCurrentX() - lassoStartX())"
                  [style.height.px]="Math.abs(lassoCurrentY() - lassoStartY())"></div>
              }

              <!-- SVG Bezier Connections Layer -->
              <svg class="middt-wf-svg-layer" xmlns="http://www.w3.org/2000/svg">
                <defs>
                  <marker id="ng-wf-arrow-default" markerWidth="10" markerHeight="8" refX="9" refY="4" orient="auto">
                    <path d="M0,0 L10,4 L0,8 Z" fill="#64748b" />
                  </marker>
                  <marker id="ng-wf-arrow-selected" markerWidth="10" markerHeight="8" refX="9" refY="4" orient="auto">
                    <path d="M0,0 L10,4 L0,8 Z" fill="#2563eb" />
                  </marker>
                  <marker id="ng-wf-arrow-start" markerWidth="10" markerHeight="8" refX="9" refY="4" orient="auto">
                    <path d="M0,0 L10,4 L0,8 Z" fill="#0284c7" />
                  </marker>
                </defs>

                @if (startTargetEdge(); as stEdge) {
                  <g
                    class="middt-wf-edge-group"
                    (pointerdown)="$event.stopPropagation()"
                    (click)="$event.stopPropagation(); selectStart()">
                    <path [attr.d]="stEdge.path" class="middt-wf-edge-hit" />
                    <path [attr.d]="stEdge.path" class="middt-wf-edge-path-start" marker-end="url(#ng-wf-arrow-start)" />
                  </g>
                }

                @for (edge of allEdges(); track edge.edgeId) {
                  @let isSel = isEdgeSelected(edge.edgeId);
                  <g
                    class="middt-wf-edge-group"
                    (pointerdown)="$event.stopPropagation()"
                    (click)="$event.stopPropagation(); onEdgeClick($event, edge.fromKey, edge.transitionIndex)">
                    <path [attr.d]="edge.geometry.path" class="middt-wf-edge-hit" />
                    <path
                      [attr.d]="edge.geometry.path"
                      [attr.class]="isSel ? 'middt-wf-edge-path-selected' : 'middt-wf-edge-path'"
                      [attr.marker-end]="isSel ? 'url(#ng-wf-arrow-selected)' : 'url(#ng-wf-arrow-default)'" />
                  </g>
                }

                @if (previewWirePath(); as previewD) {
                  <path
                    [attr.d]="previewD"
                    class="middt-wf-edge-path-preview"
                    marker-end="url(#ng-wf-arrow-selected)" />
                }
              </svg>

              <!-- Interactive Transition Badges on Midpoints of SVG Edges -->
              @for (edge of allEdges(); track edge.edgeId) {
                @let isSel = isEdgeSelected(edge.edgeId);
                <div
                  [class]="isSel ? 'middt-wf-edge-badge middt-wf-edge-badge-active' : 'middt-wf-edge-badge'"
                  [style.left.px]="edge.geometry.midX"
                  [style.top.px]="edge.geometry.midY"
                  title="Oku seçmek için tıklayın (Shift+Tık: çoklu seçim) veya üzerine görev sürükleyin"
                  (pointerdown)="$event.stopPropagation()"
                  (click)="$event.stopPropagation(); onEdgeClick($event, edge.fromKey, edge.transitionIndex)"
                  (dragover)="$event.preventDefault(); $event.stopPropagation()"
                  (drop)="$event.stopPropagation(); onDropOntoTransitionBadge($event, edge.fromKey, edge.transitionIndex)">
                  <span>{{ triggerIcon(edge.trigger) }} {{ edge.label }}</span>
                  @if (edge.taskCount > 0) {
                    <span class="middt-ui-badge middt-ui-badge-primary" style="font-size: .65rem; padding: .05rem .35rem">
                      ⚡{{ edge.taskCount }}
                    </span>
                  }
                  <button
                    type="button"
                    class="middt-wf-edge-del"
                    title="Bu geçiş okunu sil"
                    (click)="$event.stopPropagation(); removeSingleEdge(edge.fromKey, edge.transitionIndex)">
                    ✕
                  </button>
                </div>
              }

              <!-- Start Node on 2D Board -->
              <div
                [class]="getStartNodeClass()"
                [style.left.px]="getNodePos(startNodeKey).x"
                [style.top.px]="getNodePos(startNodeKey).y"
                (pointerdown)="$event.stopPropagation(); onNodeHeaderPointerDown($event, startNodeKey)">
                <div
                  class="middt-wf-board-node-handle"
                  title="Taşımak için basılı tutup sürükleyin">
                  <div>
                    <div class="middt-wf-node-title">🚀 Başlangıç (start)</div>
                    <div class="middt-wf-node-key">Hedef: {{ getString(startNode(), 'target') || '-' }}</div>
                  </div>
                  <span class="middt-ui-badge middt-ui-badge-info">Start</span>
                </div>

                <div class="middt-wf-node-section">
                  <span class="middt-wf-node-section-label">onExecute Görevleri ({{ getTasks(startNode(), 'onExecute').length }})</span>
                  <div
                    [class]="hoverDropKey() === 'start:onExecute' ? 'middt-wf-dropzone middt-wf-dropzone-over' : 'middt-wf-dropzone'"
                    (dragenter)="hoverDropKey.set('start:onExecute')"
                    (dragleave)="clearHoverDrop('start:onExecute')"
                    (dragover)="$event.preventDefault(); $event.stopPropagation()"
                    (drop)="$event.stopPropagation(); onDropOntoHook($event, startNodeKey, 'onExecute')">
                    @if (getTasks(startNode(), 'onExecute').length === 0) {
                      <div class="middt-wf-dropzone-empty">+ Görevi buraya sürükleyin</div>
                    }
                    @for (taskObj of getTasks(startNode(), 'onExecute'); track $index) {
                      <button
                        type="button"
                        draggable="true"
                        [class]="
                          selectedNodeKey() === startNodeKey && selectedTaskHook() === 'onExecute' && selectedTaskIndex() === $index
                            ? 'middt-wf-task-pill middt-wf-task-pill-active'
                            : 'middt-wf-task-pill'
                        "
                        (pointerdown)="$event.stopPropagation()"
                        (click)="$event.stopPropagation(); selectStartTask($index)"
                        (dragstart)="$event.stopPropagation(); startExistingTaskDrag($event, startNodeKey, 'onExecute', $index)">
                        <span>⚡ {{ taskSummary(taskObj) }}</span>
                        <small>{{ taskBadges(taskObj) }}</small>
                      </button>
                    }
                  </div>
                </div>

                <button
                  type="button"
                  [class]="
                    pointerMode() === 'connect-wire' && pointerSourceNodeKey() === startNodeKey
                      ? 'middt-wf-port middt-wf-port-out middt-wf-port-active'
                      : 'middt-wf-port middt-wf-port-out'
                  "
                  title="Başlangıç okunu çizmek için basılı tutup hedef duruma sürükleyin"
                  (pointerdown)="$event.stopPropagation(); onOutputPortPointerDown($event, startNodeKey)"
                  (click)="$event.stopPropagation()"></button>
              </div>

              <!-- State Nodes on 2D Board -->
              @for (stateObj of states(); track getString(stateObj, 'key') || $index) {
                @let stateKey = getString(stateObj, 'key');
                @let stateType = getString(stateObj, 'type') || 'Intermediate';
                @let stateSubType = getString(stateObj, 'subType') || 'None';
                @let pos = getNodePos(stateKey);
                <div
                  [class]="getBoardNodeClass(stateKey, stateType, stateSubType)"
                  [style.left.px]="pos.x"
                  [style.top.px]="pos.y"
                  (pointerenter)="onNodePointerEnter(stateKey)"
                  (pointerleave)="onNodePointerLeave(stateKey)"
                  (pointerup)="onNodePointerUp($event, stateKey)"
                  (pointerdown)="$event.stopPropagation(); onNodeHeaderPointerDown($event, stateKey)"
                  (dragover)="$event.preventDefault()"
                  (drop)="$event.stopPropagation(); onDropOntoStateNode($event, stateKey)">
                  <!-- Input Port (Left) -->
                  <button
                    type="button"
                    [class]="
                      hoverConnectTargetKey() === stateKey
                        ? 'middt-wf-port middt-wf-port-in middt-wf-port-active'
                        : 'middt-wf-port middt-wf-port-in'
                    "
                    title="Geçiş okunu buraya bırakın"
                    (pointerup)="$event.stopPropagation(); onNodePointerUp($event, stateKey)"
                    (pointerdown)="$event.stopPropagation()"></button>

                  <!-- Mouse-Draggable Header (Supports Single & Group Move!) -->
                  <div
                    class="middt-wf-board-node-handle"
                    title="Taşımak için basılı tutup sürükleyin (Çoklu seçimde tüm grubu taşır)">
                    <div>
                      <div class="middt-wf-node-title">{{ getString(stateObj, 'title') || stateKey }}</div>
                      <div class="middt-wf-node-key">
                        {{ stateKey }}
                        @if (stateObj['step'] !== undefined && stateObj['step'] !== null) {
                          · Adım {{ stateObj['step'] }}
                        }
                      </div>
                    </div>
                    <span class="middt-ui-badge" [class]="stateBadgeClass(stateType, stateSubType)">
                      {{ stateType }}{{ stateSubType !== 'None' ? '/' + stateSubType : '' }}
                    </span>
                  </div>

                  @if (stateType === 'SubFlow') {
                    <small class="middt-ui-text-muted">
                      🔗 Alt Akış:
                      <code>{{ getNestedString(stateObj, 'subFlow', 'workflow') || '(seçilmedi)' }}</code>
                      @if (getNestedString(stateObj, 'subFlow', 'into'); as sfOut) {
                        → <code>{{ sfOut }}</code>
                      }
                    </small>
                  } @else if (getNestedString(stateObj, 'view', 'layout'); as layoutKey) {
                    <button
                      type="button"
                      class="middt-wf-task-pill"
                      style="margin-top: .15rem"
                      title="Bu durumun ekranını (view.layout) görsel ekran tasarımcısında düzenle"
                      (pointerdown)="$event.stopPropagation()"
                      (click)="$event.stopPropagation(); openScreenModal(stateKey, layoutKey)">
                      <span>📄 Ekran: <code>{{ layoutKey }}</code></span>
                      <small>🎨 Düzenle</small>
                    </button>
                  }

                  <!-- onEntry Dropzone -->
                  <div class="middt-wf-node-section">
                    <span class="middt-wf-node-section-label">onEntry Görevleri ({{ getTasks(stateObj, 'onEntry').length }})</span>
                    <div
                      [class]="
                        hoverDropKey() === stateKey + ':onEntry'
                          ? 'middt-wf-dropzone middt-wf-dropzone-over'
                          : 'middt-wf-dropzone'
                      "
                      (dragenter)="hoverDropKey.set(stateKey + ':onEntry')"
                      (dragleave)="clearHoverDrop(stateKey + ':onEntry')"
                      (dragover)="$event.preventDefault(); $event.stopPropagation()"
                      (drop)="$event.stopPropagation(); onDropOntoHook($event, stateKey, 'onEntry')">
                      @if (getTasks(stateObj, 'onEntry').length === 0) {
                        <div class="middt-wf-dropzone-empty">+ Görevi buraya sürükleyin</div>
                      }
                      @for (tObj of getTasks(stateObj, 'onEntry'); track $index) {
                        <button
                          type="button"
                          draggable="true"
                          [class]="
                            selectedNodeKey() === stateKey && selectedTaskHook() === 'onEntry' && selectedTaskIndex() === $index
                              ? 'middt-wf-task-pill middt-wf-task-pill-active'
                              : 'middt-wf-task-pill'
                          "
                          (pointerdown)="$event.stopPropagation()"
                          (click)="$event.stopPropagation(); selectStateTask(stateKey, 'onEntry', $index)"
                          (dragstart)="$event.stopPropagation(); startExistingTaskDrag($event, stateKey, 'onEntry', $index)">
                          <span>⚡ {{ taskSummary(tObj) }}</span>
                          <small>{{ taskBadges(tObj) }}</small>
                        </button>
                      }
                    </div>
                  </div>

                  <!-- onExit Dropzone (Not available on Finish states because the workflow ends here) -->
                  @if (stateType !== 'Finish') {
                    <div class="middt-wf-node-section">
                      <span class="middt-wf-node-section-label">onExit Görevleri ({{ getTasks(stateObj, 'onExit').length }})</span>
                      <div
                        [class]="
                          hoverDropKey() === stateKey + ':onExit'
                            ? 'middt-wf-dropzone middt-wf-dropzone-over'
                            : 'middt-wf-dropzone'
                        "
                        (dragenter)="hoverDropKey.set(stateKey + ':onExit')"
                        (dragleave)="clearHoverDrop(stateKey + ':onExit')"
                        (dragover)="$event.preventDefault(); $event.stopPropagation()"
                        (drop)="$event.stopPropagation(); onDropOntoHook($event, stateKey, 'onExit')">
                        @if (getTasks(stateObj, 'onExit').length === 0) {
                          <div class="middt-wf-dropzone-empty">+ Çıkış görevi sürükleyin</div>
                        }
                        @for (tObj of getTasks(stateObj, 'onExit'); track $index) {
                          <button
                            type="button"
                            draggable="true"
                            [class]="
                              selectedNodeKey() === stateKey && selectedTaskHook() === 'onExit' && selectedTaskIndex() === $index
                                ? 'middt-wf-task-pill middt-wf-task-pill-active'
                                : 'middt-wf-task-pill'
                            "
                            (pointerdown)="$event.stopPropagation()"
                            (click)="$event.stopPropagation(); selectStateTask(stateKey, 'onExit', $index)"
                            (dragstart)="$event.stopPropagation(); startExistingTaskDrag($event, stateKey, 'onExit', $index)">
                            <span>🚪 {{ taskSummary(tObj) }}</span>
                            <small>{{ taskBadges(tObj) }}</small>
                          </button>
                        }
                      </div>
                    </div>
                  }

                  <!-- Output Port (Right) — Press and drag with mouse to draw an arrow! -->
                  @if (stateType !== 'Finish') {
                    <button
                      type="button"
                      [class]="
                        pointerMode() === 'connect-wire' && pointerSourceNodeKey() === stateKey
                          ? 'middt-wf-port middt-wf-port-out middt-wf-port-active'
                          : 'middt-wf-port middt-wf-port-out'
                      "
                      title="Ok çizmek için basılı tutup hedef duruma sürükleyin"
                      (pointerdown)="$event.stopPropagation(); onOutputPortPointerDown($event, stateKey)"
                      (click)="$event.stopPropagation()"></button>
                  }
                </div>
              }
            </div>

            <div class="middt-wf-canvas-hint">
              <span>🖱️ <strong>Seç / Odakla:</strong> Boşta sürükleyerek kutu seçimi, nesneye tıklayıp odaklanma</span>
              <span>·</span>
              <span>✋ <strong>El Aracı:</strong> Tuvali sola / sağa / yukarı / aşağı kaydır</span>
              <span>·</span>
              <span><strong>Sağ noktadan (●) sürükle:</strong> Ok çiz</span>
            </div>
          </div>

          <!-- 3. Right Context-Sensitive Inspector -->
          @if (showInspector()) {
          <aside class="middt-wf-inspector" aria-label="Seçili öğe özellikleri">
            @if (selectedNodeKeys().size + selectedEdgeIds().size > 1) {
              <!-- Multi-Selection Group Summary Inspector -->
              <div class="middt-ui-form">
                <span class="middt-ui-badge middt-ui-badge-primary">📦 Çoklu Grup Seçimi</span>
                <h3 class="middt-ui-section-title">
                  {{ selectedNodeKeys().size }} Durum ve {{ selectedEdgeIds().size }} Geçiş Oku Seçili
                </h3>
                <small class="middt-ui-text-muted">
                  Seçili durumlardan herhangi birinin başlığını fareyle sürükleyerek tüm grubu birlikte
                  taşıyabilirsiniz.
                </small>

                <div class="middt-ui-toolbar">
                  @if (selectedNodeKeys().size > 1) {
                    <button mat-stroked-button type="button" (click)="alignSelectedHorizontal()">
                      <mat-icon>align_horizontal_left</mat-icon> Yatay Hizala
                    </button>
                    <button mat-stroked-button type="button" (click)="alignSelectedVertical()">
                      <mat-icon>align_vertical_top</mat-icon> Dikey Hizala
                    </button>
                  }
                  @if (selectedDeletableNodeCount() > 0) {
                    <button mat-stroked-button type="button" (click)="duplicateSelectedStates()">
                      <mat-icon>content_copy</mat-icon> Grubu Çoğalt
                    </button>
                  }
                  <button mat-flat-button color="warn" type="button" (click)="deleteSelection()">
                    <mat-icon>delete</mat-icon> Seçilenlerin Tümünü Sil
                  </button>
                </div>

                @if (selectedNodeKeys().size > 0) {
                  <hr class="middt-ui-divider" />
                  <h4 class="middt-ui-section-title">📍 Seçili Durumlar ({{ selectedNodeKeys().size }})</h4>
                  @for (nk of Array.from(selectedNodeKeys()); track nk) {
                    <button type="button" class="middt-wf-edge-pill" (click)="selectSingleState(nk)">
                      <span>📍 {{ nk === startNodeKey ? 'Başlangıç (start)' : nk }}</span>
                      <small>Tek Seç →</small>
                    </button>
                  }
                }

                @if (selectedEdgeIds().size > 0) {
                  <hr class="middt-ui-divider" />
                  <h4 class="middt-ui-section-title">🔀 Seçili Geçiş Okları ({{ selectedEdgeIds().size }})</h4>
                  @for (eid of Array.from(selectedEdgeIds()); track eid) {
                    <div class="middt-wf-edge-pill">
                      <span>🔀 {{ eid }}</span>
                    </div>
                  }
                }
              </div>
            } @else if (selectedTaskObject(); as taskObj) {
              @let tType = getString(taskObj, 'task') || 'set';
              @let spec = findTaskSpec(tType);
              @let cfg = getConfig(taskObj);
              @let errAction = getErrorAction(taskObj);

              <div class="middt-ui-form">
                <div class="middt-ui-toolbar" style="justify-content: space-between">
                  <div>
                    <span class="middt-ui-badge middt-ui-badge-primary">⚡ Görev ({{ selectedTaskHook() }})</span>
                    <h3 class="middt-ui-section-title" style="margin-top: .25rem">{{ spec?.title ?? tType }}</h3>
                  </div>
                  <div class="middt-ui-toolbar">
                    <button mat-button type="button" [disabled]="selectedTaskIndex() <= 0" (click)="moveSelectedTask(-1)">↑</button>
                    <button mat-button type="button" (click)="moveSelectedTask(1)">↓</button>
                    <button mat-button color="warn" type="button" (click)="deleteSelectedTask()">Sil</button>
                  </div>
                </div>

                @if (spec) {
                  <small class="middt-ui-text-muted">{{ spec.description }}</small>
                }

                <div class="middt-wf-grid-2">
                  <div class="middt-ui-input">
                    <label>Görev Anahtarı (key)</label>
                    <input
                      class="middt-wf-input"
                      [value]="getString(taskObj, 'key')"
                      (change)="setString(taskObj, 'key', $any($event.target).value)" />
                  </div>
                  <div class="middt-ui-input">
                    <label>Görev Tipi (task)</label>
                    <select
                      class="middt-wf-input"
                      [value]="tType"
                      (change)="changeTaskType(taskObj, $any($event.target).value)">
                      @for (s of taskSpecs(); track s.name) {
                        <option [value]="s.name">{{ s.title }}</option>
                      }
                    </select>
                  </div>
                </div>

                <div class="middt-ui-input">
                  <label>Çalışma Koşulu (when — C# İfadesi)</label>
                  <input
                    class="middt-wf-input"
                    placeholder="data.amount >= 50000"
                    [value]="getString(taskObj, 'when')"
                    (change)="setString(taskObj, 'when', $any($event.target).value)" />
                </div>

                @if (selectedTaskHook() === 'onEntry' && selectedNodeKey() !== startNodeKey) {
                  <label class="middt-ui-check">
                    <input
                      type="checkbox"
                      [checked]="taskObj['background'] === true"
                      (change)="setBool(taskObj, 'background', $any($event.target).checked)" />
                    <span>Arka Planda Çalıştır (<code>background: true</code>)</span>
                  </label>
                }

                <div class="middt-wf-grid-2">
                  <div class="middt-ui-input">
                    <label>Hata Politikası (onError.action)</label>
                    <select
                      class="middt-wf-input"
                      [value]="errAction"
                      (change)="setErrorAction(taskObj, $any($event.target).value)">
                      <option value="Fault">Fault (Faulted Durumuna Geç / Varsayılan)</option>
                      <option value="Continue">Continue (Hatayı Kaydet ve Devam Et)</option>
                      <option value="Transition">Transition (Hata Geçişine Yönlendir)</option>
                    </select>
                  </div>
                  <div class="middt-ui-input">
                    <label>Deneme Sayısı / Bekleme (maxAttempts / delay)</label>
                    <div class="middt-ui-picker">
                      <input
                        type="number"
                        class="middt-wf-input"
                        style="width: 70px"
                        min="1"
                        [value]="getErrorInt(taskObj, 'maxAttempts', 1)"
                        (change)="setErrorAttempts(taskObj, $any($event.target).value)" />
                      <input
                        class="middt-wf-input"
                        [value]="getErrorString(taskObj, 'delay') || '00:00:01'"
                        (change)="setErrorString(taskObj, 'delay', $any($event.target).value)" />
                    </div>
                  </div>
                  @if (errAction === 'Transition') {
                    <div class="middt-ui-input">
                      <label>Hata Geçiş Anahtarı (onError.transition)</label>
                      <input
                        class="middt-wf-input"
                        placeholder="to-error-state"
                        [value]="getErrorString(taskObj, 'transition')"
                        (change)="setErrorString(taskObj, 'transition', $any($event.target).value)" />
                    </div>
                  }
                </div>

                <hr class="middt-ui-divider" />
                <h4 class="middt-ui-section-title">🔧 Görev Parametreleri (config)</h4>

                @if (spec && spec.params.length > 0) {
                  @for (param of spec.params; track param.name) {
                    <div class="middt-ui-input">
                      <label>
                        {{ param.label }}
                        @if (param.required) {
                          <span style="color: #dc2626">*</span>
                        }
                      </label>
                      @if (param.type === 'boolean') {
                        <label class="middt-ui-check">
                          <input
                            type="checkbox"
                            [checked]="getParamBool(cfg, param.name, param.defaultValue)"
                            (change)="setParamBool(cfg, param.name, $any($event.target).checked)" />
                          <span>{{ param.description }}</span>
                        </label>
                      } @else if (param.type === 'enum') {
                        <select
                          class="middt-wf-input"
                          [value]="getString(cfg, param.name) || param.defaultValue || ''"
                          (change)="setString(cfg, param.name, $any($event.target).value)">
                          @for (opt of param.options; track opt) {
                            <option [value]="opt">{{ opt || '(Seçiniz / Yok)' }}</option>
                          }
                        </select>
                        <small class="middt-ui-text-muted">{{ param.description }}</small>
                      } @else if (param.type === 'number') {
                        <input
                          type="number"
                          class="middt-wf-input"
                          [value]="cfg[param.name] ?? param.defaultValue ?? ''"
                          (change)="setIntNullable(cfg, param.name, $any($event.target).value)" />
                        <small class="middt-ui-text-muted">{{ param.description }}</small>
                      } @else if (param.type === 'json') {
                        <textarea
                          class="middt-wf-input middt-wf-code-box"
                          spellcheck="false"
                          [value]="getJsonParamText(cfg, param.name)"
                          (change)="setJsonParam(cfg, param.name, $any($event.target).value)"></textarea>
                        <small class="middt-ui-text-muted">{{ param.description }}</small>
                      } @else {
                        <input
                          class="middt-wf-input"
                          [placeholder]="param.defaultValue ?? ''"
                          [value]="getString(cfg, param.name)"
                          (change)="setString(cfg, param.name, $any($event.target).value)" />
                        <small class="middt-ui-text-muted">{{ param.description }}</small>
                      }
                    </div>
                  }
                }
              </div>
            } @else if (selectedTransitionObject(); as trObj) {
              @let trTrigger = getString(trObj, 'trigger') || 'Manual';
              @let trTasks = getTasks(trObj, 'onExecute');

              <div class="middt-ui-form">
                <div class="middt-ui-toolbar" style="justify-content: space-between">
                  <div>
                    <span class="middt-ui-badge middt-ui-badge-info">🔀 Geçiş (Transition)</span>
                    <h3 class="middt-ui-section-title" style="margin-top: .25rem">
                      {{ getString(trObj, 'title') || getString(trObj, 'key') }}
                    </h3>
                    <small class="middt-ui-text-muted">
                      <code>{{ selectedNodeKey() }}</code> → <code>{{ getString(trObj, 'target') }}</code>
                    </small>
                  </div>
                  <button mat-button color="warn" type="button" (click)="deleteSelectedTransition()">Oku Sil</button>
                </div>

                <div class="middt-wf-grid-2">
                  <div class="middt-ui-input">
                    <label>Geçiş Anahtarı (key)</label>
                    <input
                      class="middt-wf-input"
                      [value]="getString(trObj, 'key')"
                      (change)="setString(trObj, 'key', $any($event.target).value)" />
                  </div>
                  <div class="middt-ui-input">
                    <label>Başlık / Buton Adı (title)</label>
                    <input
                      class="middt-wf-input"
                      [value]="getString(trObj, 'title')"
                      (change)="setString(trObj, 'title', $any($event.target).value)" />
                  </div>
                  <div class="middt-ui-input">
                    <label>Hedef Durum (target)</label>
                    <select
                      class="middt-wf-input"
                      [value]="getString(trObj, 'target') || '$self'"
                      (change)="setString(trObj, 'target', $any($event.target).value)">
                      <option value="$self">$self (Aynı Durumda Kal)</option>
                      @for (st of states(); track getString(st, 'key')) {
                        <option [value]="getString(st, 'key')">
                          {{ getString(st, 'key') }} ({{ getString(st, 'title') || getString(st, 'key') }})
                        </option>
                      }
                    </select>
                  </div>
                  <div class="middt-ui-input">
                    <label>Tetikleyici Türü (trigger)</label>
                    <select
                      class="middt-wf-input"
                      [value]="trTrigger"
                      (change)="setString(trObj, 'trigger', $any($event.target).value)">
                      <option value="Manual">Manual (Kullanıcı Butonu / API)</option>
                      <option value="Automatic">Automatic (Koşullu Otomatik)</option>
                      <option value="Scheduled">Scheduled (Zamanlayıcı / SLA)</option>
                      <option value="Event">Event (Mesaj / Olay Tetikli)</option>
                    </select>
                  </div>
                </div>

                <div class="middt-ui-input">
                  <label>Koşul Kuralı (rule — C# İfadesi)</label>
                  <input
                    class="middt-wf-input"
                    placeholder="data.amount <= 50000"
                    [value]="getString(trObj, 'rule')"
                    (change)="setString(trObj, 'rule', $any($event.target).value)" />
                </div>

                <div class="middt-ui-input">
                  <label>Açıklama (description — AI Ajan Yönlendirmesi için)</label>
                  <input
                    class="middt-wf-input"
                    [value]="getString(trObj, 'description')"
                    (change)="setString(trObj, 'description', $any($event.target).value)" />
                </div>

                @if (trTrigger === 'Manual') {
                  <div class="middt-wf-grid-2">
                    <div class="middt-ui-input">
                      <label>Buton Stili (style)</label>
                      <select
                        class="middt-wf-input"
                        [value]="getString(trObj, 'style')"
                        (change)="setString(trObj, 'style', $any($event.target).value)">
                        <option value="">Varsayılan</option>
                        <option value="primary">primary (Mavi)</option>
                        <option value="success">success (Yeşil)</option>
                        <option value="warning">warning (Sarı)</option>
                        <option value="danger">danger (Kırmızı)</option>
                        <option value="secondary">secondary (Gri)</option>
                      </select>
                    </div>
                    <div class="middt-ui-input">
                      <label>Yetkili Roller (roles)</label>
                      <input
                        class="middt-wf-input"
                        placeholder="manager, $starter"
                        [value]="getCsvArray(trObj, 'roles')"
                        (change)="setCsvArray(trObj, 'roles', $any($event.target).value)" />
                    </div>
                  </div>
                } @else if (trTrigger === 'Automatic') {
                  <label class="middt-ui-check">
                    <input
                      type="checkbox"
                      [checked]="trObj['default'] === true"
                      (change)="setBool(trObj, 'default', $any($event.target).checked)" />
                    <span>Varsayılan Dal (<code>default: true</code>)</span>
                  </label>
                } @else if (trTrigger === 'Scheduled') {
                  <div class="middt-ui-input">
                    <label>Bekleme Süresi (after)</label>
                    <input
                      class="middt-wf-input"
                      placeholder="00:10:00 veya PT10M"
                      [value]="getString(trObj, 'after')"
                      (change)="setString(trObj, 'after', $any($event.target).value)" />
                  </div>
                } @else if (trTrigger === 'Event') {
                  <div class="middt-ui-input">
                    <label>Beklenen Olay Adı (event)</label>
                    <input
                      class="middt-wf-input"
                      placeholder="PaymentCaptured"
                      [value]="getString(trObj, 'event')"
                      (change)="setString(trObj, 'event', $any($event.target).value)" />
                  </div>
                }

                <hr class="middt-ui-divider" />
                <h4 class="middt-ui-section-title">⚡ Geçiş Görevleri (onExecute: {{ trTasks.length }})</h4>
                <div
                  class="middt-wf-dropzone"
                  (dragover)="$event.preventDefault()"
                  (drop)="onDropOntoTransitionBadge($event, selectedNodeKey(), selectedTransitionIndex())">
                  @if (trTasks.length === 0) {
                    <div class="middt-wf-dropzone-empty">
                      Sol paletten bir görevi buraya veya tuvaldeki geçiş etiketine sürükleyin
                    </div>
                  }
                  @for (tObj of trTasks; track $index) {
                    <button
                      type="button"
                      class="middt-wf-task-pill"
                      (click)="selectTransitionTask(selectedNodeKey(), selectedTransitionIndex(), $index)">
                      <span>⚡ {{ taskSummary(tObj) }}</span>
                      <small>{{ taskBadges(tObj) }}</small>
                    </button>
                  }
                </div>
              </div>
            } @else if (selectedState(); as stateObj) {
              @let stateKey = getString(stateObj, 'key');
              @let trs = getTransitions(stateObj);

              <div class="middt-ui-form">
                <div class="middt-ui-toolbar" style="justify-content: space-between">
                  <div>
                    <span class="middt-ui-badge middt-ui-badge-primary">📍 Durum (State)</span>
                    <h3 class="middt-ui-section-title" style="margin-top: .25rem">
                      {{ getString(stateObj, 'title') || stateKey }}
                    </h3>
                  </div>
                  <button mat-button color="warn" type="button" (click)="removeState(stateKey)">Durumu Sil</button>
                </div>

                <div class="middt-wf-grid-2">
                  <div class="middt-ui-input">
                    <label>Durum Anahtarı (key)</label>
                    <input
                      class="middt-wf-input"
                      [value]="stateKey"
                      (change)="renameState(stateObj, $any($event.target).value)" />
                  </div>
                  <div class="middt-ui-input">
                    <label>Durum Başlığı (title)</label>
                    <input
                      class="middt-wf-input"
                      [value]="getString(stateObj, 'title')"
                      (change)="setString(stateObj, 'title', $any($event.target).value)" />
                  </div>
                  <div class="middt-ui-input">
                    <label>Durum Tipi (type)</label>
                    <select
                      class="middt-wf-input"
                      [value]="getString(stateObj, 'type') || 'Intermediate'"
                      (change)="changeStateType(stateObj, $any($event.target).value)">
                      <option value="Initial">Initial</option>
                      <option value="Intermediate">Intermediate</option>
                      <option value="SubFlow">SubFlow</option>
                      <option value="Finish">Finish</option>
                    </select>
                  </div>
                  <div class="middt-ui-input">
                    <label>Alt Tip (subType)</label>
                    <select
                      class="middt-wf-input"
                      [value]="getString(stateObj, 'subType') || 'None'"
                      (change)="setString(stateObj, 'subType', $any($event.target).value)">
                      <option value="None">None (Sistem / Otomatik)</option>
                      <option value="Human">Human (İnsan Görevi)</option>
                      <option value="Success">Success (Başarılı Sonuç)</option>
                      <option value="Error">Error (Red / Hata)</option>
                      <option value="Cancelled">Cancelled (İptal)</option>
                      <option value="Timeout">Timeout (Zaman Aşımı)</option>
                    </select>
                  </div>
                  <div class="middt-ui-input">
                    <label>İlerleme Çubuğu Adım Sırası (step)</label>
                    <input
                      type="number"
                      class="middt-wf-input"
                      placeholder="Ör: 1, 2, 3"
                      [value]="stateObj['step'] ?? ''"
                      (change)="setIntNullable(stateObj, 'step', $any($event.target).value)" />
                    <small class="middt-ui-text-muted">
                      Kayıt detay ekranının üstündeki ilerleme çubuğunda (1 → 2 → 3) kaçıncı aşama olarak görüneceğini
                      belirler. Boş bırakılırsa adım çubuğunda yer almaz.
                    </small>
                  </div>
                  @if (getString(stateObj, 'type') !== 'SubFlow') {
                    @let curViewLayout = getNestedString(stateObj, 'view', 'layout');
                    <div class="middt-ui-input">
                      <label>Ekran Yerleşimi (view.layout)</label>
                      <div class="middt-ui-picker">
                        <select
                          class="middt-wf-input"
                          style="flex: 1"
                          [value]="curViewLayout"
                          (change)="setNestedString(stateObj, 'view', 'layout', $any($event.target).value)">
                          <option value="">(Ekran seçiniz veya aşağıdan yeni oluşturun)</option>
                          @for (ly of uiLayouts(); track ly.key) {
                            <option [value]="ly.key">{{ ly.key }} ({{ ly.title ?? ly.key }} · {{ ly.kind }})</option>
                          }
                        </select>
                        <input
                          class="middt-wf-input"
                          style="max-width: 170px"
                          placeholder="credit-review"
                          [value]="curViewLayout"
                          (change)="setNestedString(stateObj, 'view', 'layout', $any($event.target).value)" />
                      </div>
                      <div class="middt-ui-toolbar" style="margin-top: .25rem">
                        @if (curViewLayout) {
                          <button mat-stroked-button type="button" (click)="openScreenModal(stateKey, curViewLayout)">
                            🎨 Ekranı Tasarla / Düzenle ({{ curViewLayout }})
                          </button>
                        }
                        <button mat-stroked-button type="button" (click)="createAndBindScreenModal(stateObj)">
                          ✨ Yeni Ekran Oluştur & Bağla
                        </button>
                        @if (curViewLayout) {
                          <button
                            mat-stroked-button
                            type="button"
                            title="Yerleşim Editörü sayfasında tam ekran aç"
                            (click)="openScreenStandalone(curViewLayout)">
                            ↗ Bağımsız Aç
                          </button>
                        }
                      </div>
                    </div>
                  } @else {
                    <div class="middt-ui-input">
                      <label>Ekran Yerleşimi (Alt Akıştan Otomatik)</label>
                      <small class="middt-ui-text-muted">
                        🔗 <strong>SubFlow</strong> durumlarında ayrı bir ekran tanımlanmaz; çalışma zamanında çağrılan
                        alt akışın kendi aktif adım ekranı (<code>view.layout</code>) otomatik olarak gösterilir.
                      </small>
                    </div>
                  }
                </div>

                @if (getString(stateObj, 'type') === 'SubFlow') {
                  @let subFlowObj = getSubFlow(stateObj);
                  @let sfTarget = getString(subFlowObj, 'workflow');
                  @let sfInputs = getSubFlowInputEntries(subFlowObj);

                  <hr class="middt-ui-divider" />
                  <div class="middt-ui-toolbar" style="justify-content: space-between">
                    <h4 class="middt-ui-section-title">🔗 Alt Akış Ayarları (subFlow)</h4>
                    @if (sfTarget) {
                      <button mat-stroked-button type="button" (click)="openWorkflow(sfTarget)">
                        <mat-icon>open_in_new</mat-icon> '{{ sfTarget }}' Akışını Aç
                      </button>
                    }
                  </div>

                  <div class="middt-wf-grid-2">
                    <div class="middt-ui-input">
                      <label>Çağrılacak Alt Akış (subFlow.workflow)</label>
                      <select
                        class="middt-wf-input"
                        [value]="sfTarget"
                        (change)="setString(subFlowObj, 'workflow', $any($event.target).value)">
                        <option value="">(Alt akış seçiniz)</option>
                        @for (w of otherWorkflows(); track w.key) {
                          <option [value]="w.key">{{ w.key }} ({{ w.title ?? w.key }})</option>
                        }
                      </select>
                    </div>
                    <div class="middt-ui-input">
                      <label>Sonucun Yazılacağı Alan (subFlow.into)</label>
                      <input
                        class="middt-wf-input"
                        placeholder="Ör: riskResult (boşsa kök veriye birleştirir)"
                        [value]="getString(subFlowObj, 'into')"
                        (change)="setString(subFlowObj, 'into', $any($event.target).value)" />
                    </div>
                  </div>

                  <label class="middt-ui-check">
                    <input
                      type="checkbox"
                      [checked]="subFlowObj['forwardTransitions'] !== false"
                      (change)="setExplicitBool(subFlowObj, 'forwardTransitions', $any($event.target).checked)" />
                    <span>
                      Alt akışın manuel geçiş butonlarını üst akış ekranından doğrudan tetiklemeye izin ver
                      (<code>forwardTransitions: true</code>)
                    </span>
                  </label>

                  <div class="middt-ui-toolbar" style="justify-content: space-between; margin-top: .35rem">
                    <label style="font-size: .78rem; font-weight: 600">
                      Alt Akışa Gönderilecek Giriş Verileri (subFlow.input: {{ sfInputs.length }})
                    </label>
                    <button mat-stroked-button type="button" (click)="addSubFlowInputRow(subFlowObj)">
                      <mat-icon>add</mat-icon> Parametre Ekle
                    </button>
                  </div>
                  <small class="middt-ui-text-muted">
                    Boş bırakılırsa üst akışın tüm verisi alt akışa kopyalanır.
                  </small>
                  @for (inp of sfInputs; track inp.key) {
                    <div class="middt-ui-picker">
                      <input
                        class="middt-wf-input"
                        style="max-width: 150px"
                        placeholder="hedefAlan"
                        [value]="inp.key"
                        (change)="renameSubFlowInputKey(subFlowObj, inp.key, $any($event.target).value)" />
                      <span>=</span>
                      <input
                        class="middt-wf-input"
                        style="flex: 1"
                        placeholder="data.customerKey"
                        [value]="inp.value"
                        (change)="setSubFlowInputValue(subFlowObj, inp.key, $any($event.target).value)" />
                      <button
                        mat-icon-button
                        color="warn"
                        type="button"
                        (click)="removeSubFlowInputKey(subFlowObj, inp.key)">
                        <mat-icon>delete</mat-icon>
                      </button>
                    </div>
                  }
                }

                @if (getString(stateObj, 'type') !== 'Finish') {
                  <hr class="middt-ui-divider" />
                  <div class="middt-ui-toolbar" style="justify-content: space-between">
                    <h4 class="middt-ui-section-title">🔀 Çıkan Geçişler ({{ trs.length }})</h4>
                    <button mat-stroked-button type="button" (click)="addTransition(stateObj)">
                      <mat-icon>add</mat-icon> Geçiş Ekle
                    </button>
                  </div>
                  <small class="middt-ui-text-muted">
                    İpucu: Tuvalde durumun sağındaki mavi noktadan (●) başka bir duruma basılı tutup sürükleyerek canlı
                    ok çizebilirsiniz.
                  </small>
                  @for (trItem of trs; track $index) {
                    <div class="middt-ui-picker">
                      <button
                        type="button"
                        class="middt-wf-edge-pill"
                        style="flex: 1"
                        (click)="selectTransition(stateKey, $index)">
                        <span>
                          {{ triggerIcon(getString(trItem, 'trigger')) }}
                          {{ getString(trItem, 'title') || getString(trItem, 'key') }} →
                          {{ getString(trItem, 'target') }}
                        </span>
                        <small>Düzenle →</small>
                      </button>
                      <button
                        mat-icon-button
                        color="warn"
                        type="button"
                        title="Bu geçiş okunu sil"
                        (click)="removeSingleEdge(stateKey, $index)">
                        <mat-icon>delete</mat-icon>
                      </button>
                    </div>
                  }
                } @else {
                  <hr class="middt-ui-divider" />
                  <small class="middt-ui-text-muted">
                    🏁 <strong>Finish (Bitiş)</strong> durumlarında akış sonlandığı için çıkış görevi
                    (<code>onExit</code>) veya çıkan geçiş oku (<code>transitions</code>) eklenemez; yalnızca giriş
                    görevleri (<code>onEntry</code>) çalışır.
                  </small>
                }
              </div>
            } @else {
              <!-- Default: Workflow Root & Start Node Inspector -->
              <div class="middt-ui-form">
                <span class="middt-ui-badge middt-ui-badge-info">🚀 İş Akışı & Başlangıç</span>
                <h3 class="middt-ui-section-title">{{ getString(doc(), 'title') || 'İş Akışı Ayarları' }}</h3>

                <div class="middt-wf-grid-2">
                  <div class="middt-ui-input">
                    <label>Akış Anahtarı (key)</label>
                    <input
                      class="middt-wf-input"
                      [disabled]="selectedWorkflowKey() !== null"
                      [value]="getString(doc(), 'key')"
                      (change)="setString(doc(), 'key', $any($event.target).value)" />
                  </div>
                  <div class="middt-ui-input">
                    <label>Akış Başlığı (title)</label>
                    <input
                      class="middt-wf-input"
                      [value]="getString(doc(), 'title')"
                      (change)="setString(doc(), 'title', $any($event.target).value)" />
                  </div>
                  <div class="middt-ui-input">
                    <label>Menü İkonu (icon)</label>
                    <input
                      class="middt-wf-input"
                      placeholder="account_tree"
                      [value]="getString(doc(), 'icon')"
                      (change)="setString(doc(), 'icon', $any($event.target).value)" />
                  </div>
                  <div class="middt-ui-input">
                    <label>Tarihçe Modu (history)</label>
                    <select
                      class="middt-wf-input"
                      [value]="getString(doc(), 'history') || 'Full'"
                      (change)="setString(doc(), 'history', $any($event.target).value)">
                      <option value="Full">Full (Tam Snapshot)</option>
                      <option value="Delta">Delta (JSON Patch Farkı)</option>
                      <option value="None">None (Tarihçe Tutma)</option>
                    </select>
                  </div>
                  <div class="middt-ui-input">
                    <label>Erişim Modu (instanceAccess.mode)</label>
                    <select
                      class="middt-wf-input"
                      [value]="getAccessMode()"
                      (change)="setAccessMode($any($event.target).value)">
                      <option value="all">all (Yetkili Herkes Görür)</option>
                      <option value="owner">owner (Sadece Başlatan ve Supervisor)</option>
                    </select>
                  </div>
                  <div class="middt-ui-input">
                    <label>Roller (roles, virgülle)</label>
                    <input
                      class="middt-wf-input"
                      [value]="getCsvArray(doc(), 'roles')"
                      (change)="setCsvArray(doc(), 'roles', $any($event.target).value)" />
                  </div>
                </div>

                <hr class="middt-ui-divider" />
                <h4 class="middt-ui-section-title">🚀 Başlangıç (start) Ayarları</h4>
                <div class="middt-ui-input">
                  <label>İlk Hedef Durum (start.target)</label>
                  <select
                    class="middt-wf-input"
                    [value]="getString(startNode(), 'target')"
                    (change)="setString(startNode(), 'target', $any($event.target).value)">
                    @for (st of states(); track getString(st, 'key')) {
                      <option [value]="getString(st, 'key')">
                        {{ getString(st, 'key') }} ({{ getString(st, 'title') || getString(st, 'key') }})
                      </option>
                    }
                  </select>
                </div>
                <div class="middt-ui-input">
                  <label>Başlangıç Giriş Kuralı (start.input.rule)</label>
                  <input
                    class="middt-wf-input"
                    placeholder="input.amount > 0"
                    [value]="getNestedString(startNode(), 'input', 'rule')"
                    (change)="setNestedString(startNode(), 'input', 'rule', $any($event.target).value)" />
                </div>
              </div>
            }
          </aside>
          }
        </div>
      } @else if (activeTab() === 'schema') {
        <mat-card appearance="outlined">
          <mat-card-content>
            <div class="middt-ui-form">
              <div class="middt-ui-toolbar" style="justify-content: space-between">
                <div>
                  <h3 class="middt-ui-section-title">🗂️ İş Akışı Veri Şeması (dataSchema)</h3>
                  <small class="middt-ui-text-muted">
                    Burada tanımlanan alanlar liste kolonlarını, arama filtrelerini ve PostgreSQL bölüm (partition)
                    indekslerini otomatik oluşturur.
                  </small>
                </div>
                <div class="middt-ui-toolbar">
                  <input
                    class="middt-wf-input"
                    style="width: 180px"
                    placeholder="Yeni alan adı (ör. amount)"
                    [value]="newSchemaFieldName()"
                    (input)="newSchemaFieldName.set($any($event.target).value)" />
                  <button mat-stroked-button type="button" (click)="addSchemaField()">
                    <mat-icon>add</mat-icon> Alan Ekle
                  </button>
                </div>
              </div>

              @for (entry of schemaProperties(); track entry.name) {
                <div class="middt-wf-task-card">
                  <div class="middt-ui-toolbar" style="justify-content: space-between">
                    <strong><code>{{ entry.name }}</code> — {{ getString(entry.spec, 'title') || entry.name }}</strong>
                    <button mat-button color="warn" type="button" (click)="removeSchemaField(entry.name)">Sil</button>
                  </div>
                  <div class="middt-wf-grid-3">
                    <div class="middt-ui-input">
                      <label>Başlık (title)</label>
                      <input
                        class="middt-wf-input"
                        [value]="getString(entry.spec, 'title')"
                        (change)="setString(entry.spec, 'title', $any($event.target).value)" />
                    </div>
                    <div class="middt-ui-input">
                      <label>Veri Tipi (type)</label>
                      <select
                        class="middt-wf-input"
                        [value]="getString(entry.spec, 'type') || 'string'"
                        (change)="setString(entry.spec, 'type', $any($event.target).value)">
                        <option value="string">string (Metin)</option>
                        <option value="number">number (Ondalıklı Sayı / Tutar)</option>
                        <option value="integer">integer (Tam Sayı)</option>
                        <option value="boolean">boolean (Evet / Hayır)</option>
                        <option value="array">array (Dizi / Liste)</option>
                        <option value="object">object (Alt Nesne)</option>
                      </select>
                    </div>
                    <div class="middt-ui-input">
                      <label>Maskeleme (x-mask)</label>
                      <select
                        class="middt-wf-input"
                        [value]="getString(entry.spec, 'x-mask')"
                        (change)="setString(entry.spec, 'x-mask', $any($event.target).value)">
                        <option value="">Maskeleme Yok</option>
                        <option value="middle">middle (Ortasını Maskele)</option>
                        <option value="end">end (Sonunu Maskele)</option>
                      </select>
                    </div>
                    <label class="middt-ui-check">
                      <input
                        type="checkbox"
                        [checked]="isRequiredField(entry.name)"
                        (change)="toggleRequiredField(entry.name, $any($event.target).checked)" />
                      <span>Zorunlu Alan (required)</span>
                    </label>
                    <label class="middt-ui-check">
                      <input
                        type="checkbox"
                        [checked]="entry.spec['x-search'] === true"
                        (change)="setBool(entry.spec, 'x-search', $any($event.target).checked)" />
                      <span>Arama Filtresi & İndeks (x-search)</span>
                    </label>
                    <label class="middt-ui-check">
                      <input
                        type="checkbox"
                        [checked]="entry.spec['x-list'] !== false"
                        (change)="setExplicitBool(entry.spec, 'x-list', $any($event.target).checked)" />
                      <span>Listede Kolon Göster (x-list)</span>
                    </label>
                  </div>
                </div>
              }
            </div>
          </mat-card-content>
        </mat-card>
      } @else if (activeTab() === 'json') {
        <mat-card appearance="outlined">
          <mat-card-content>
            <div class="middt-ui-form">
              <div class="middt-ui-toolbar" style="justify-content: space-between">
                <label for="wf-definition-json" class="middt-ui-text-muted">
                  İş Akışı Tanımı JSON (Sürükle-bırak tuval ile çift yönlü senkronize)
                </label>
                <button mat-stroked-button type="button" (click)="formatJson()">
                  <mat-icon>data_object</mat-icon> JSON Biçimlendir
                </button>
              </div>
              <textarea
                id="wf-definition-json"
                class="middt-ui-editor"
                spellcheck="false"
                [value]="json()"
                (input)="onRawJsonInput($any($event.target).value)"></textarea>
            </div>
          </mat-card-content>
        </mat-card>
      }

      @if (screenModalOpen()) {
        <div class="middt-wf-modal-backdrop" (click)="closeScreenModal()">
          <div class="middt-wf-modal" (click)="$event.stopPropagation()">
            <div class="middt-wf-modal-header">
              <div>
                <strong>🖥️ Görsel Ekran & Yerleşim Tasarımcısı</strong>
                @if (screenModalTargetStateKey(); as tgtState) {
                  <span class="middt-ui-badge middt-ui-badge-primary" style="margin-left: .5rem">
                    Durum: {{ tgtState }}
                  </span>
                }
              </div>
              <div class="middt-ui-toolbar">
                @if (screenModalInitialKey(); as initK) {
                  <button mat-stroked-button type="button" (click)="openScreenStandalone(initK)">
                    ↗ Bağımsız Tam Ekran Aç
                  </button>
                }
                <button mat-stroked-button type="button" (click)="closeScreenModal()">✕ Kapat</button>
              </div>
            </div>
            <div class="middt-wf-modal-body">
              <middt-layout-editor
                [embedded]="true"
                [initialKey]="screenModalInitialKey()"
                [initialWorkflowKey]="getString(doc(), 'key')"
                [initialDocument]="screenModalStarterDoc()"
                [workflowFieldsInput]="currentWorkflowFieldHints()"
                (savedKey)="onScreenModalSaved($event)"
                (closed)="closeScreenModal()" />
            </div>
          </div>
        </div>
      }
    </div>
  `,
})
export class MiddtWorkflowDesignerComponent implements OnInit {
  readonly #client = inject(WORKFLOW_API_CLIENT);
  readonly #uiClient = inject(UI_API_CLIENT, { optional: true });
  readonly #router = inject(Router, { optional: true });
  readonly #snack = inject(MatSnackBar);
  protected readonly boardEl = viewChild<ElementRef<HTMLElement>>('boardEl');

  readonly changed = output<void>();

  protected readonly Math = Math;
  protected readonly Array = Array;
  protected readonly startNodeKey = START_NODE_KEY;
  protected readonly stateTemplates = STATE_TEMPLATES;
  protected readonly workflows = signal<WorkflowSummaryModel[]>([]);
  protected readonly taskSpecs = signal<WorkflowTaskSpecModel[]>([]);
  protected readonly uiLayouts = signal<UiLayoutSummary[]>([]);
  protected readonly screenModalOpen = signal<boolean>(false);
  protected readonly screenModalTargetStateKey = signal<string | null>(null);
  protected readonly screenModalInitialKey = signal<string | null>(null);
  protected readonly screenModalStarterDoc = signal<UiLayoutDocument | null>(null);
  protected readonly errors = signal<string[]>([]);
  protected readonly validMessage = signal<string | null>(null);
  protected readonly selectedWorkflowKey = signal<string | null>(null);
  protected readonly selectedNodeKey = signal<string>(START_NODE_KEY);
  protected readonly selectedNodeKeys = signal<Set<string>>(new Set());
  protected readonly selectedEdgeIds = signal<Set<string>>(new Set());
  protected readonly selectedTaskHook = signal<string>('onEntry');
  protected readonly selectedTaskIndex = signal<number>(-1);
  protected readonly selectedTransitionIndex = signal<number>(-1);
  protected readonly activeTab = signal<'visual' | 'schema' | 'json'>('visual');
  protected readonly paletteFilter = signal<string>('');
  protected readonly newSchemaFieldName = signal<string>('');
  protected readonly hoverDropKey = signal<string | null>(null);
  protected readonly hoverConnectTargetKey = signal<string | null>(null);
  protected readonly zoom = signal<number>(1.0);
  protected readonly panX = signal<number>(0);
  protected readonly panY = signal<number>(0);
  protected readonly toolMode = signal<'select' | 'pan'>('select');
  protected readonly showPalette = signal<boolean>(true);
  protected readonly showInspector = signal<boolean>(true);
  protected readonly busy = signal<boolean>(false);
  protected readonly doc = signal<WorkflowData>(createStarterWorkflow());
  protected readonly json = signal<string>('');
  readonly #nodePositions = signal<Record<string, NodePoint>>({});

  // Mouse / Pointer interaction signals ("move-nodes" | "connect-wire" | "lasso" | "pan" | null)
  protected readonly pointerMode = signal<'move-nodes' | 'connect-wire' | 'lasso' | 'pan' | null>(null);
  protected readonly pointerSourceNodeKey = signal<string | null>(null);
  protected readonly wireCursorX = signal<number>(0);
  protected readonly wireCursorY = signal<number>(0);
  protected readonly lassoStartX = signal<number>(0);
  protected readonly lassoStartY = signal<number>(0);
  protected readonly lassoCurrentX = signal<number>(0);
  protected readonly lassoCurrentY = signal<number>(0);

  #pointerStartClientX = 0;
  #pointerStartClientY = 0;
  #panStartX = 0;
  #panStartY = 0;
  #pointerMoved = false;
  #wireStartX = 0;
  #wireStartY = 0;
  #moveInitialPositions = new Map<string, NodePoint>();

  // HTML5 Palette Drag state
  #dragKind: 'palette-state' | 'palette-task' | 'move-task' | null = null;
  #dragStateType = '';
  #dragStateSubType = '';
  #dragStateTitle = '';
  #dragTaskType = '';
  #dragSourceNodeKey = '';
  #dragSourceHook = '';
  #dragSourceIndex = -1;

  protected readonly selectedDeletableNodeCount = computed(() => {
    let count = 0;
    for (const k of this.selectedNodeKeys()) {
      if (k !== START_NODE_KEY) {
        count++;
      }
    }

    return count;
  });

  protected readonly previewWirePath = computed<string | null>(() => {
    const mode = this.pointerMode();
    const src = this.pointerSourceNodeKey();
    if (mode !== 'connect-wire' || !src) {
      return null;
    }

    const fromPos = this.getNodePos(src);
    const fromWidth = src === START_NODE_KEY ? START_NODE_WIDTH : STATE_NODE_WIDTH;
    const x1 = fromPos.x + fromWidth;
    const y1 = fromPos.y + PORT_OFFSET_Y;
    const x2 = this.wireCursorX();
    const y2 = this.wireCursorY();
    const dx = Math.max(70, Math.abs(x2 - x1) * 0.45);
    return `M ${x1} ${y1} C ${x1 + dx} ${y1}, ${x2 - dx} ${y2}, ${x2} ${y2}`;
  });

  protected readonly filteredTaskGroups = computed(() => {
    const filter = this.paletteFilter().trim().toLowerCase();
    const specs = filter
      ? this.taskSpecs().filter(
          t =>
            t.name.toLowerCase().includes(filter) ||
            t.title.toLowerCase().includes(filter) ||
            t.category.toLowerCase().includes(filter),
        )
      : this.taskSpecs();
    const map = new Map<string, WorkflowTaskSpecModel[]>();
    for (const s of specs) {
      const list = map.get(s.category) ?? [];
      list.push(s);
      map.set(s.category, list);
    }

    return Array.from(map.entries()).map(([category, items]) => ({ category, items }));
  });

  protected readonly states = computed<WorkflowData[]>(() => {
    const raw = this.doc()['states'];
    return Array.isArray(raw) ? (raw.filter(x => x && typeof x === 'object') as WorkflowData[]) : [];
  });

  protected readonly startNode = computed<WorkflowData>(() => {
    const d = this.doc();
    if (!d['start'] || typeof d['start'] !== 'object' || Array.isArray(d['start'])) {
      d['start'] = { key: 'start', target: '' };
    }

    return d['start'] as WorkflowData;
  });

  protected readonly selectedState = computed<WorkflowData | null>(() => {
    const key = this.selectedNodeKey();
    if (!key || key === START_NODE_KEY) {
      return null;
    }

    return this.states().find(s => s['key'] === key) ?? null;
  });

  protected readonly selectedTaskObject = computed<WorkflowData | null>(() => {
    const idx = this.selectedTaskIndex();
    if (idx < 0) {
      return null;
    }

    const arr = this.#getHookArray(this.selectedNodeKey(), this.selectedTaskHook());
    return arr && idx < arr.length ? arr[idx] ?? null : null;
  });

  protected readonly selectedTransitionObject = computed<WorkflowData | null>(() => {
    const idx = this.selectedTransitionIndex();
    const st = this.selectedState();
    if (idx < 0 || !st) {
      return null;
    }

    const trs = this.getTransitions(st);
    return idx < trs.length ? trs[idx] ?? null : null;
  });

  protected readonly startTargetEdge = computed<EdgeGeometry | null>(() => {
    this.#nodePositions();
    const targetKey = this.getString(this.startNode(), 'target');
    if (!targetKey || !this.states().some(s => s['key'] === targetKey)) {
      return null;
    }

    return this.#computeEdgeGeometry(START_NODE_KEY, targetKey, 0, 1);
  });

  protected readonly allEdges = computed<CanvasEdge[]>(() => {
    this.#nodePositions();
    const list: CanvasEdge[] = [];
    const sts = this.states();
    const pairCounts = new Map<string, number>();

    for (const st of sts) {
      const fromKey = this.getString(st, 'key');
      for (const tr of this.getTransitions(st)) {
        const rawTarget = this.getString(tr, 'target') || '$self';
        const resolvedTarget = rawTarget === '$self' ? fromKey : rawTarget;
        const pairKey = `${fromKey}->${resolvedTarget}`;
        pairCounts.set(pairKey, (pairCounts.get(pairKey) ?? 0) + 1);
      }
    }

    const pairSeen = new Map<string, number>();
    for (const st of sts) {
      const fromKey = this.getString(st, 'key');
      const trs = this.getTransitions(st);
      for (let i = 0; i < trs.length; i++) {
        const tr = trs[i]!;
        const rawTarget = this.getString(tr, 'target') || '$self';
        const resolvedTarget = rawTarget === '$self' ? fromKey : rawTarget;
        if (!sts.some(s => s['key'] === resolvedTarget)) {
          continue;
        }

        const pairKey = `${fromKey}->${resolvedTarget}`;
        const idxInPair = pairSeen.get(pairKey) ?? 0;
        pairSeen.set(pairKey, idxInPair + 1);
        const totalInPair = pairCounts.get(pairKey) ?? 1;

        const geometry = this.#computeEdgeGeometry(fromKey, resolvedTarget, idxInPair, totalInPair, i, trs.length);
        const key = this.getString(tr, 'key') || `t-${i + 1}`;
        const label = this.getString(tr, 'title') || key;
        const trigger = this.getString(tr, 'trigger') || 'Manual';
        const taskCount = this.getTasks(tr, 'onExecute').length;

        list.push({
          edgeId: `${fromKey}:${i}`,
          fromKey,
          targetKey: resolvedTarget,
          transitionIndex: i,
          key,
          label,
          trigger,
          taskCount,
          geometry,
        });
      }
    }

    return list;
  });

  protected readonly schemaProperties = computed<{ name: string; spec: WorkflowData }[]>(() => {
    const d = this.doc();
    const schema = d['dataSchema'] as WorkflowData | undefined;
    const props = schema?.['properties'] as WorkflowData | undefined;
    if (!props || typeof props !== 'object') {
      return [];
    }

    return Object.entries(props)
      .filter(([, v]) => v && typeof v === 'object' && !Array.isArray(v))
      .map(([name, v]) => ({ name, spec: v as WorkflowData }));
  });

  protected readonly currentWorkflowFieldHints = computed<WorkflowSchemaFieldHint[]>(() =>
    this.schemaProperties().map(sp => {
      const t = this.getString(sp.spec, 'type') || 'string';
      const fmt = this.getString(sp.spec, 'format');
      return {
        field: sp.name,
        title: this.getString(sp.spec, 'title') || sp.name,
        type: t,
        format:
          fmt || (t === 'number' || t === 'integer' ? 'number' : t === 'boolean' ? 'boolean' : ''),
      };
    }),
  );

  async ngOnInit(): Promise<void> {
    this.#ensureNodePositions(true);
    this.#syncJsonFromDoc();
    const [wfResp, specsResp, layoutsResp] = await Promise.all([
      this.#client.listWorkflows(),
      this.#client.listTaskSpecs(),
      this.#uiClient
        ? this.#uiClient.list()
        : Promise.resolve({ result: 0, messageList: [], count: 0, data: [] as UiLayoutSummary[] }),
    ]);

    if (isSuccess(wfResp) && wfResp.data) {
      this.workflows.set(wfResp.data);
    }

    if (isSuccess(specsResp) && specsResp.data) {
      this.taskSpecs.set(specsResp.data);
    }

    if (isSuccess(layoutsResp) && layoutsResp.data) {
      this.uiLayouts.set(layoutsResp.data);
    }

    const first = this.workflows()[0];
    if (first) {
      await this.openWorkflow(first.key);
    }
  }

  protected openScreenModal(stateKey: string | null, layoutKey: string | null): void {
    this.screenModalTargetStateKey.set(stateKey);
    this.screenModalInitialKey.set(layoutKey && layoutKey.trim() ? layoutKey : null);
    this.screenModalStarterDoc.set(null);
    this.screenModalOpen.set(true);
  }

  protected createAndBindScreenModal(stateObj: WorkflowData): void {
    const stateKey = this.getString(stateObj, 'key') || 'state';
    const stateTitle = this.getString(stateObj, 'title') || stateKey;
    const wfKey = this.getString(this.doc(), 'key') || 'workflow';
    const baseKey = `${wfKey}-${stateKey}-view`;
    let newLayoutKey = baseKey;
    let idx = 2;
    while (this.uiLayouts().some(l => l.key === newLayoutKey)) {
      newLayoutKey = `${baseKey}-${idx++}`;
    }

    this.setNestedString(stateObj, 'view', 'layout', newLayoutKey);

    const fieldItems = this.schemaProperties()
      .slice(0, 8)
      .map(sp => {
        const t = this.getString(sp.spec, 'type');
        const fmt =
          this.getString(sp.spec, 'format') ||
          (t === 'number' || t === 'integer' ? 'number' : t === 'boolean' ? 'boolean' : '');
        const item: Record<string, unknown> = {
          label: this.getString(sp.spec, 'title') || sp.name,
          value: `{{ data.${sp.name} }}`,
        };
        if (fmt) {
          item['format'] = fmt;
        }
        return item;
      });

    if (fieldItems.length === 0) {
      fieldItems.push({ label: 'Kayıt Anahtarı', value: '{{ instance.key }}' });
      fieldItems.push({ label: 'Mevcut Durum', value: '{{ instance.state }}' });
    }

    const starter: UiLayoutDocument = {
      key: newLayoutKey,
      version: 1,
      title: `${stateTitle} Ekranı`,
      description: `${wfKey} iş akışı '${stateTitle}' durumu için görsel ekran yerleşimi`,
      kind: 'view',
      roles: ['Admin', 'Approver', 'Operator', 'Auditor'],
      body: [
        {
          widget: 'section',
          title: stateTitle,
          subtitle: 'İş akışı veri şemasından otomatik oluşturulan özet alanlar',
          icon: 'fact_check',
          children: [
            {
              widget: 'fields',
              columns: 3,
              items: fieldItems,
            },
          ],
        },
      ],
    };

    this.screenModalTargetStateKey.set(stateKey);
    this.screenModalInitialKey.set(null);
    this.screenModalStarterDoc.set(starter);
    this.screenModalOpen.set(true);
  }

  protected async onScreenModalSaved(savedLayoutKey: string): Promise<void> {
    if (this.#uiClient) {
      const resp = await this.#uiClient.list();
      if (isSuccess(resp) && resp.data) {
        this.uiLayouts.set(resp.data);
      }
    }

    const targetStateKey = this.screenModalTargetStateKey();
    if (targetStateKey && savedLayoutKey.trim()) {
      const stObj = this.states().find(s => this.getString(s, 'key') === targetStateKey);
      if (stObj && this.getString(stObj, 'type') !== 'SubFlow') {
        this.setNestedString(stObj, 'view', 'layout', savedLayoutKey);
      }
    }

    this.screenModalInitialKey.set(savedLayoutKey);
  }

  protected closeScreenModal(): void {
    this.screenModalOpen.set(false);
    this.screenModalTargetStateKey.set(null);
    this.screenModalStarterDoc.set(null);
  }

  protected openScreenStandalone(layoutKey: string): void {
    const wfKey = this.getString(this.doc(), 'key');
    const queryParams: Record<string, string> = {};
    if (layoutKey) {
      queryParams['key'] = layoutKey;
    }
    if (wfKey) {
      queryParams['workflow'] = wfKey;
    }
    this.#router?.navigate(['/ui-admin'], { queryParams });
  }

  protected async onSelectWorkflow(key: string): Promise<void> {
    if (!key) {
      this.newWorkflow();
      return;
    }

    await this.openWorkflow(key);
  }

  protected async openWorkflow(key: string): Promise<void> {
    this.errors.set([]);
    this.validMessage.set(null);
    const resp = await this.#client.getDefinitionJson(key);
    if (!isSuccess(resp) || !resp.data) {
      this.errors.set(resp.messageList);
      return;
    }

    this.selectedWorkflowKey.set(key);
    this.doc.set(structuredClone(resp.data));
    this.clearSelection();
    this.#ensureNodePositions(!resp.data['x-layout']);
    this.#syncJsonFromDoc();
  }

  protected newWorkflow(): void {
    this.errors.set([]);
    this.validMessage.set(null);
    this.selectedWorkflowKey.set(null);
    this.doc.set(createStarterWorkflow());
    this.clearSelection();
    this.#ensureNodePositions(true);
    this.#syncJsonFromDoc();
  }

  protected switchTab(tab: 'visual' | 'schema' | 'json'): void {
    if (this.activeTab() === 'json' && tab !== 'json') {
      this.#tryParseRawJson();
    } else {
      this.#syncJsonFromDoc();
    }

    this.activeTab.set(tab);
  }

  protected autoLayoutBoard(): void {
    this.#ensureNodePositions(true);
    this.#touchDoc();
  }

  protected changeZoom(delta: number): void {
    const next = Math.min(1.5, Math.max(0.5, Math.round((this.zoom() + delta) * 100) / 100));
    this.zoom.set(next);
  }

  // --- Mouse / Pointer Canvas Interactions (Pan, Move Group, Draw Arrow, Marquee Lasso) ---

  protected getStudioBodyClass(): string {
    if (this.showPalette() && this.showInspector()) {
      return 'middt-wf-studio-body';
    }
    if (!this.showPalette() && this.showInspector()) {
      return 'middt-wf-studio-body middt-wf-studio-body-no-left';
    }
    if (this.showPalette() && !this.showInspector()) {
      return 'middt-wf-studio-body middt-wf-studio-body-no-right';
    }
    return 'middt-wf-studio-body middt-wf-studio-body-full';
  }

  protected getViewportClass(): string {
    if (this.pointerMode() === 'pan') {
      return 'middt-wf-viewport middt-wf-viewport-pan middt-wf-viewport-panning';
    }

    return this.toolMode() === 'pan'
      ? 'middt-wf-viewport middt-wf-viewport-pan'
      : 'middt-wf-viewport middt-wf-viewport-select';
  }

  protected resetViewport(): void {
    this.panX.set(0);
    this.panY.set(0);
    this.zoom.set(1.0);
  }

  #startPan(event: PointerEvent): void {
    this.pointerMode.set('pan');
    this.#pointerMoved = false;
    this.#pointerStartClientX = event.clientX;
    this.#pointerStartClientY = event.clientY;
    this.#panStartX = this.panX();
    this.#panStartY = this.panY();
  }

  protected onViewportPointerDown(event: PointerEvent): void {
    if (this.toolMode() === 'pan' || event.button === 1 || event.altKey) {
      this.#startPan(event);
      return;
    }

    this.onBoardPointerDown(event);
  }

  protected onBoardPointerDown(event: PointerEvent): void {
    if (this.toolMode() === 'pan' || event.button === 1 || event.altKey) {
      this.#startPan(event);
      return;
    }

    if (event.button !== 0) {
      return;
    }

    const additive = event.shiftKey || event.ctrlKey || event.metaKey;
    if (!additive) {
      this.selectedNodeKeys.set(new Set());
      this.selectedEdgeIds.set(new Set());
      this.selectedNodeKey.set(START_NODE_KEY);
      this.selectedTaskIndex.set(-1);
      this.selectedTransitionIndex.set(-1);
    }

    const pt = this.#clientToBoardPoint(event.clientX, event.clientY);
    this.pointerMode.set('lasso');
    this.#pointerMoved = false;
    this.#pointerStartClientX = event.clientX;
    this.#pointerStartClientY = event.clientY;
    this.lassoStartX.set(pt.x);
    this.lassoStartY.set(pt.y);
    this.lassoCurrentX.set(pt.x);
    this.lassoCurrentY.set(pt.y);
  }

  protected onNodeHeaderPointerDown(event: PointerEvent, nodeKey: string): void {
    if (this.toolMode() === 'pan' || event.button === 1 || event.altKey) {
      this.#startPan(event);
      return;
    }

    if (event.button !== 0) {
      return;
    }

    const additive = event.shiftKey || event.ctrlKey || event.metaKey;
    const currentSet = new Set(this.selectedNodeKeys());
    if (additive) {
      if (!currentSet.delete(nodeKey)) {
        currentSet.add(nodeKey);
      }
      this.selectedNodeKeys.set(currentSet);
    } else if (!currentSet.has(nodeKey)) {
      this.selectedNodeKeys.set(new Set([nodeKey]));
      this.selectedEdgeIds.set(new Set());
    }

    this.selectedNodeKey.set(nodeKey);
    this.selectedTaskIndex.set(-1);
    this.selectedTransitionIndex.set(-1);

    this.pointerMode.set('move-nodes');
    this.pointerSourceNodeKey.set(nodeKey);
    this.#pointerMoved = false;
    this.#pointerStartClientX = event.clientX;
    this.#pointerStartClientY = event.clientY;
    this.#moveInitialPositions.clear();

    for (const k of this.selectedNodeKeys()) {
      this.#moveInitialPositions.set(k, this.getNodePos(k));
    }
    if (!this.#moveInitialPositions.has(nodeKey)) {
      this.#moveInitialPositions.set(nodeKey, this.getNodePos(nodeKey));
    }
  }

  protected onOutputPortPointerDown(event: PointerEvent, fromNodeKey: string): void {
    if (event.button !== 0) {
      return;
    }

    const pos = this.getNodePos(fromNodeKey);
    const width = fromNodeKey === START_NODE_KEY ? START_NODE_WIDTH : STATE_NODE_WIDTH;
    this.#wireStartX = pos.x + width;
    this.#wireStartY = pos.y + PORT_OFFSET_Y;
    this.wireCursorX.set(this.#wireStartX + 20);
    this.wireCursorY.set(this.#wireStartY);
    this.#pointerMoved = false;
    this.#pointerStartClientX = event.clientX;
    this.#pointerStartClientY = event.clientY;
    this.pointerMode.set('connect-wire');
    this.pointerSourceNodeKey.set(fromNodeKey);
    this.hoverConnectTargetKey.set(null);
  }

  protected onBoardPointerMove(event: PointerEvent): void {
    const mode = this.pointerMode();
    if (!mode) {
      return;
    }

    const rawDx = event.clientX - this.#pointerStartClientX;
    const rawDy = event.clientY - this.#pointerStartClientY;
    if (Math.abs(rawDx) > 3 || Math.abs(rawDy) > 3) {
      this.#pointerMoved = true;
    }

    if (mode === 'pan') {
      this.panX.set(Math.min(900, Math.max(-2400, Math.round(this.#panStartX + rawDx))));
      this.panY.set(Math.min(700, Math.max(-1600, Math.round(this.#panStartY + rawDy))));
      return;
    }

    const dx = rawDx / this.zoom();
    const dy = rawDy / this.zoom();

    if (mode === 'move-nodes') {
      for (const [key, initPos] of this.#moveInitialPositions.entries()) {
        const nx = Math.max(16, snap(initPos.x + dx));
        const ny = Math.max(16, snap(initPos.y + dy));
        this.#setNodePos(key, nx, ny);
      }
    } else if (mode === 'connect-wire') {
      const cx = this.#wireStartX + dx;
      const cy = this.#wireStartY + dy;
      this.wireCursorX.set(cx);
      this.wireCursorY.set(cy);
      this.hoverConnectTargetKey.set(this.#findStateAtPoint(cx, cy));
    } else if (mode === 'lasso') {
      this.lassoCurrentX.set(Math.max(0, this.lassoStartX() + dx));
      this.lassoCurrentY.set(Math.max(0, this.lassoStartY() + dy));
      this.#updateLassoSelection();
    }
  }

  protected onBoardPointerUp(event: PointerEvent): void {
    const mode = this.pointerMode();
    if (mode === 'move-nodes') {
      if (this.#pointerMoved) {
        this.#touchDoc();
      } else {
        const src = this.pointerSourceNodeKey();
        if (src && !event.shiftKey && !event.ctrlKey && !event.metaKey) {
          this.selectSingleState(src);
        }
      }
    } else if (mode === 'connect-wire') {
      const src = this.pointerSourceNodeKey();
      const target = this.hoverConnectTargetKey() ?? this.#findStateAtPoint(this.wireCursorX(), this.wireCursorY());
      if (src && target && target !== START_NODE_KEY) {
        this.#completeConnection(src, target);
      }
    }

    this.cancelPointerAction();
  }

  protected onNodePointerEnter(stateKey: string): void {
    if (this.pointerMode() === 'connect-wire') {
      this.hoverConnectTargetKey.set(stateKey);
    }
  }

  protected onNodePointerLeave(stateKey: string): void {
    if (this.pointerMode() === 'connect-wire' && this.hoverConnectTargetKey() === stateKey) {
      this.hoverConnectTargetKey.set(null);
    }
  }

  protected onNodePointerUp(event: PointerEvent, targetStateKey: string): void {
    const src = this.pointerSourceNodeKey();
    if (this.pointerMode() === 'connect-wire' && src && targetStateKey !== START_NODE_KEY) {
      this.#completeConnection(src, targetStateKey);
      this.cancelPointerAction();
      return;
    }

    if (this.pointerMode() === 'move-nodes') {
      if (this.#pointerMoved) {
        this.#touchDoc();
      } else if (src && !event.shiftKey && !event.ctrlKey && !event.metaKey) {
        this.selectSingleState(src);
      }
      this.cancelPointerAction();
    }
  }

  protected cancelPointerAction(): void {
    this.pointerMode.set(null);
    this.pointerSourceNodeKey.set(null);
    this.hoverConnectTargetKey.set(null);
    this.#pointerMoved = false;
    this.#moveInitialPositions.clear();
  }

  #findStateAtPoint(x: number, y: number): string | null {
    for (const st of this.states()) {
      const key = this.getString(st, 'key');
      if (!key) {
        continue;
      }

      const pos = this.getNodePos(key);
      if (
        x >= pos.x - 16 &&
        x <= pos.x + STATE_NODE_WIDTH + 16 &&
        y >= pos.y - 12 &&
        y <= pos.y + NODE_APPROX_HEIGHT + 24
      ) {
        return key;
      }
    }

    return null;
  }

  #updateLassoSelection(): void {
    const minX = Math.min(this.lassoStartX(), this.lassoCurrentX());
    const maxX = Math.max(this.lassoStartX(), this.lassoCurrentX());
    const minY = Math.min(this.lassoStartY(), this.lassoCurrentY());
    const maxY = Math.max(this.lassoStartY(), this.lassoCurrentY());

    if (maxX - minX < 6 && maxY - minY < 6) {
      return;
    }

    const nextNodes = new Set<string>();
    const nextEdges = new Set<string>();

    for (const st of this.states()) {
      const key = this.getString(st, 'key');
      if (!key) {
        continue;
      }

      const pos = this.getNodePos(key);
      const intersects =
        pos.x + STATE_NODE_WIDTH >= minX &&
        pos.x <= maxX &&
        pos.y + NODE_APPROX_HEIGHT >= minY &&
        pos.y <= maxY;
      if (intersects) {
        nextNodes.add(key);
      }
    }

    for (const edge of this.allEdges()) {
      if (
        edge.geometry.midX >= minX &&
        edge.geometry.midX <= maxX &&
        edge.geometry.midY >= minY &&
        edge.geometry.midY <= maxY
      ) {
        nextEdges.add(edge.edgeId);
      }
    }

    this.selectedNodeKeys.set(nextNodes);
    this.selectedEdgeIds.set(nextEdges);

    const firstNode = Array.from(nextNodes)[0];
    if (firstNode) {
      this.selectedNodeKey.set(firstNode);
    }
  }

  // --- Multi-Selection, Group Move/Align/Duplicate/Delete & Keyboard ---

  protected selectSingleState(nodeKey: string): void {
    this.selectedNodeKeys.set(new Set([nodeKey]));
    this.selectedEdgeIds.set(new Set());
    this.selectedNodeKey.set(nodeKey);
    this.selectedTaskIndex.set(-1);
    this.selectedTransitionIndex.set(-1);
  }

  protected onEdgeClick(event: MouseEvent, fromKey: string, transitionIndex: number): void {
    const edgeId = `${fromKey}:${transitionIndex}`;
    const additive = event.shiftKey || event.ctrlKey || event.metaKey;
    if (additive) {
      const next = new Set(this.selectedEdgeIds());
      if (!next.delete(edgeId)) {
        next.add(edgeId);
      }
      this.selectedEdgeIds.set(next);
      this.selectedNodeKey.set(fromKey);
      this.selectedTransitionIndex.set(transitionIndex);
      this.selectedTaskIndex.set(-1);
      return;
    }

    this.selectedNodeKeys.set(new Set());
    this.selectedEdgeIds.set(new Set([edgeId]));
    this.selectTransition(fromKey, transitionIndex);
  }

  protected isEdgeSelected(edgeId: string): boolean {
    if (this.selectedEdgeIds().has(edgeId)) {
      return true;
    }

    return (
      this.selectedTransitionIndex() >= 0 &&
      `${this.selectedNodeKey()}:${this.selectedTransitionIndex()}` === edgeId
    );
  }

  protected selectAllOnBoard(): void {
    const nextNodes = new Set<string>();
    for (const st of this.states()) {
      const k = this.getString(st, 'key');
      if (k) {
        nextNodes.add(k);
      }
    }

    const nextEdges = new Set<string>();
    for (const edge of this.allEdges()) {
      nextEdges.add(edge.edgeId);
    }

    this.selectedNodeKeys.set(nextNodes);
    this.selectedEdgeIds.set(nextEdges);
    this.selectedTaskIndex.set(-1);
    this.selectedTransitionIndex.set(-1);
  }

  protected clearSelection(): void {
    this.selectedNodeKeys.set(new Set());
    this.selectedEdgeIds.set(new Set());
    this.selectedNodeKey.set(START_NODE_KEY);
    this.selectedTaskIndex.set(-1);
    this.selectedTransitionIndex.set(-1);
  }

  protected onCanvasKeyDown(event: KeyboardEvent): void {
    if (event.key === 'Escape') {
      this.cancelPointerAction();
      this.clearSelection();
      return;
    }

    if (event.key === 'Delete' || event.key === 'Backspace') {
      if (this.selectedTaskIndex() >= 0) {
        this.deleteSelectedTask();
      } else if (this.selectedNodeKeys().size > 0 || this.selectedEdgeIds().size > 0) {
        this.deleteSelection();
      } else if (this.selectedTransitionIndex() >= 0) {
        this.deleteSelectedTransition();
      }
    }
  }

  protected deleteSelection(): void {
    const d = this.doc();
    if (!Array.isArray(d['states'])) {
      return;
    }

    // 1. Delete selected edges in descending index order per state so indices don't shift
    const edgesByState = new Map<string, number[]>();
    for (const eid of this.selectedEdgeIds()) {
      const sep = eid.lastIndexOf(':');
      if (sep <= 0) {
        continue;
      }

      const fk = eid.slice(0, sep);
      const tidx = Number.parseInt(eid.slice(sep + 1), 10);
      if (Number.isFinite(tidx)) {
        const list = edgesByState.get(fk) ?? [];
        list.push(tidx);
        edgesByState.set(fk, list);
      }
    }

    for (const [fk, indices] of edgesByState.entries()) {
      const stObj = this.states().find(s => s['key'] === fk);
      if (!stObj) {
        continue;
      }

      const trs = this.#ensureTasksArray(stObj, 'transitions');
      for (const idx of indices.sort((a, b) => b - a)) {
        if (idx >= 0 && idx < trs.length) {
          trs.splice(idx, 1);
        }
      }
    }

    // 2. Delete selected state nodes (except Start) and clean up incoming transitions
    const nodesToRemove = Array.from(this.selectedNodeKeys()).filter(k => k !== START_NODE_KEY);
    if (nodesToRemove.length > 0) {
      const removeSet = new Set(nodesToRemove);
      d['states'] = (d['states'] as WorkflowData[]).filter(s => !removeSet.has(this.getString(s, 'key')));

      this.#nodePositions.update(prev => {
        const copy = { ...prev };
        for (const rk of removeSet) {
          delete copy[rk];
        }
        return copy;
      });

      if (d['x-layout'] && typeof d['x-layout'] === 'object') {
        const layout = d['x-layout'] as Record<string, unknown>;
        for (const rk of removeSet) {
          delete layout[rk];
        }
      }

      // Clean up transitions pointing to removed states
      for (const st of d['states'] as WorkflowData[]) {
        const trs = this.#ensureTasksArray(st, 'transitions');
        for (let i = trs.length - 1; i >= 0; i--) {
          const tgt = this.getString(trs[i], 'target');
          if (removeSet.has(tgt)) {
            trs.splice(i, 1);
          }
        }
      }
    }

    this.clearSelection();
    this.#touchDoc();
  }

  protected removeSingleEdge(fromStateKey: string, transitionIndex: number): void {
    const stObj = this.states().find(s => s['key'] === fromStateKey);
    if (!stObj) {
      return;
    }

    const trs = this.#ensureTasksArray(stObj, 'transitions');
    if (transitionIndex < 0 || transitionIndex >= trs.length) {
      return;
    }

    trs.splice(transitionIndex, 1);
    const nextEdges = new Set(this.selectedEdgeIds());
    nextEdges.delete(`${fromStateKey}:${transitionIndex}`);
    this.selectedEdgeIds.set(nextEdges);
    if (this.selectedNodeKey() === fromStateKey && this.selectedTransitionIndex() === transitionIndex) {
      this.selectedTransitionIndex.set(-1);
    }

    this.#touchDoc();
  }

  protected alignSelectedHorizontal(): void {
    const keys = Array.from(this.selectedNodeKeys());
    if (keys.length < 2) {
      return;
    }

    const avgY = snap(keys.reduce((sum, k) => sum + this.getNodePos(k).y, 0) / keys.length);
    for (const k of keys) {
      const p = this.getNodePos(k);
      this.#setNodePos(k, p.x, avgY);
    }

    this.#touchDoc();
  }

  protected alignSelectedVertical(): void {
    const keys = Array.from(this.selectedNodeKeys());
    if (keys.length < 2) {
      return;
    }

    const avgX = snap(keys.reduce((sum, k) => sum + this.getNodePos(k).x, 0) / keys.length);
    for (const k of keys) {
      const p = this.getNodePos(k);
      this.#setNodePos(k, avgX, p.y);
    }

    this.#touchDoc();
  }

  protected duplicateSelectedStates(): void {
    const d = this.doc();
    if (!Array.isArray(d['states'])) {
      return;
    }

    const statesArr = d['states'] as WorkflowData[];
    const sourceKeys = Array.from(this.selectedNodeKeys()).filter(k => k !== START_NODE_KEY);
    if (sourceKeys.length === 0) {
      return;
    }

    const newSelection = new Set<string>();
    for (const srcKey of sourceKeys) {
      const srcObj = statesArr.find(s => s['key'] === srcKey);
      if (!srcObj) {
        continue;
      }

      const clone = structuredClone(srcObj);
      let copyIdx = 2;
      let newKey = `${srcKey}-copy`;
      while (statesArr.some(s => s['key'] === newKey)) {
        newKey = `${srcKey}-copy-${copyIdx++}`;
      }

      clone['key'] = newKey;
      clone['title'] = `${this.getString(srcObj, 'title') || srcKey} (Kopya)`;
      statesArr.push(clone);

      const srcPos = this.getNodePos(srcKey);
      this.#setNodePos(newKey, srcPos.x + 48, srcPos.y + 48);
      newSelection.add(newKey);
    }

    this.selectedNodeKeys.set(newSelection);
    this.selectedEdgeIds.set(new Set());
    const first = Array.from(newSelection)[0];
    if (first) {
      this.selectedNodeKey.set(first);
    }

    this.#touchDoc();
  }

  // --- HTML5 Palette Drag & Drop Handlers ---

  protected startPaletteStateDrag(event: DragEvent, type: string, subType: string, defaultTitle: string): void {
    this.#dragKind = 'palette-state';
    this.#dragStateType = type;
    this.#dragStateSubType = subType;
    this.#dragStateTitle = defaultTitle;
    event.dataTransfer?.setData('text/plain', `state:${type}:${subType}`);
  }

  protected startPaletteTaskDrag(event: DragEvent, taskType: string): void {
    this.#dragKind = 'palette-task';
    this.#dragTaskType = taskType;
    event.dataTransfer?.setData('text/plain', `task:${taskType}`);
  }

  protected startExistingTaskDrag(event: DragEvent, nodeKey: string, hook: string, taskIndex: number): void {
    this.#dragKind = 'move-task';
    this.#dragSourceNodeKey = nodeKey;
    this.#dragSourceHook = hook;
    this.#dragSourceIndex = taskIndex;
    event.dataTransfer?.setData('text/plain', `movetask:${nodeKey}:${hook}:${taskIndex}`);
  }

  protected onBoardDrop(event: DragEvent): void {
    event.preventDefault();
    this.hoverDropKey.set(null);

    if (this.#dragKind === 'palette-state' && this.#dragStateType) {
      const pt = this.#computeBoardDropPoint(event);
      this.#createStateOnBoard(this.#dragStateType, this.#dragStateSubType, this.#dragStateTitle, pt.x, pt.y);
    } else if (this.#dragKind === 'palette-task' && this.#dragTaskType) {
      const pt = this.#computeBoardDropPoint(event);
      const stateKey = this.#createStateOnBoard('Intermediate', 'None', `${this.#dragTaskType} Adımı`, pt.x, pt.y);
      const stObj = this.states().find(s => s['key'] === stateKey);
      if (stObj) {
        const arr = this.#ensureTasksArray(stObj, 'onEntry');
        arr.push(this.#createTaskNode(this.#dragTaskType));
        this.selectStateTask(stateKey, 'onEntry', arr.length - 1);
        this.#touchDoc();
      }
    }

    this.#clearDragState();
  }

  protected onDropOntoStateNode(event: DragEvent, targetStateKey: string): void {
    event.preventDefault();
    this.hoverDropKey.set(null);

    if (this.#dragKind === 'palette-task' || this.#dragKind === 'move-task') {
      this.onDropOntoHook(event, targetStateKey, 'onEntry');
      return;
    }

    this.onBoardDrop(event);
  }

  protected readonly otherWorkflows = computed(() => {
    const curKey = this.getString(this.doc(), 'key');
    return this.workflows().filter(w => w.key !== curKey);
  });

  protected changeStateType(stateObj: WorkflowData, newType: string): void {
    this.setString(stateObj, 'type', newType);
    if (newType === 'Finish') {
      delete stateObj['onExit'];
      delete stateObj['transitions'];
      delete stateObj['subFlow'];
    } else if (newType === 'SubFlow') {
      delete stateObj['view'];
      const sf = this.getSubFlow(stateObj);
      if (!sf['workflow']) {
        sf['workflow'] = this.otherWorkflows()[0]?.key ?? '';
      }
    } else {
      delete stateObj['subFlow'];
    }
    this.#touchDoc();
  }

  protected getSubFlow(stateObj: WorkflowData): WorkflowData {
    if (!stateObj['subFlow'] || typeof stateObj['subFlow'] !== 'object' || Array.isArray(stateObj['subFlow'])) {
      stateObj['subFlow'] = {};
    }

    return stateObj['subFlow'] as WorkflowData;
  }

  protected getSubFlowInputEntries(subFlowObj: WorkflowData): { key: string; value: string }[] {
    const inp = subFlowObj['input'];
    if (!inp || typeof inp !== 'object' || Array.isArray(inp)) {
      return [];
    }

    return Object.entries(inp as Record<string, unknown>).map(([key, val]) => ({
      key,
      value: typeof val === 'string' ? val : String(val ?? ''),
    }));
  }

  protected addSubFlowInputRow(subFlowObj: WorkflowData): void {
    if (!subFlowObj['input'] || typeof subFlowObj['input'] !== 'object' || Array.isArray(subFlowObj['input'])) {
      subFlowObj['input'] = {};
    }

    const inp = subFlowObj['input'] as WorkflowData;
    let idx = Object.keys(inp).length + 1;
    let paramKey = `param${idx}`;
    while (paramKey in inp) {
      idx++;
      paramKey = `param${idx}`;
    }

    inp[paramKey] = 'data.title';
    this.#touchDoc();
  }

  protected renameSubFlowInputKey(subFlowObj: WorkflowData, oldKey: string, newKey: string): void {
    const trimmed = newKey.trim();
    if (!trimmed || trimmed === oldKey) {
      return;
    }

    const inp = subFlowObj['input'] as WorkflowData | undefined;
    if (!inp || typeof inp !== 'object') {
      return;
    }

    const existingVal = inp[oldKey];
    delete inp[oldKey];
    inp[trimmed] = existingVal ?? '';
    this.#touchDoc();
  }

  protected setSubFlowInputValue(subFlowObj: WorkflowData, key: string, expr: string): void {
    if (!subFlowObj['input'] || typeof subFlowObj['input'] !== 'object' || Array.isArray(subFlowObj['input'])) {
      subFlowObj['input'] = {};
    }

    (subFlowObj['input'] as WorkflowData)[key] = expr;
    this.#touchDoc();
  }

  protected removeSubFlowInputKey(subFlowObj: WorkflowData, key: string): void {
    const inp = subFlowObj['input'] as WorkflowData | undefined;
    if (inp && typeof inp === 'object') {
      delete inp[key];
      if (Object.keys(inp).length === 0) {
        delete subFlowObj['input'];
      }
    }

    this.#touchDoc();
  }

  protected onDropOntoHook(event: DragEvent, targetNodeKey: string, targetHook: string): void {
    event.preventDefault();
    this.hoverDropKey.set(null);
    if (targetHook === 'onExit') {
      const tgtSt = this.states().find(s => s['key'] === targetNodeKey);
      if (tgtSt && this.getString(tgtSt, 'type') === 'Finish') {
        this.#clearDragState();
        return;
      }
    }

    const targetArr = this.#getHookArray(targetNodeKey, targetHook);
    if (!targetArr) {
      this.#clearDragState();
      return;
    }

    if (this.#dragKind === 'palette-task' && this.#dragTaskType) {
      targetArr.push(this.#createTaskNode(this.#dragTaskType));
      this.selectedNodeKey.set(targetNodeKey);
      this.selectedTransitionIndex.set(-1);
      this.selectedTaskHook.set(targetHook);
      this.selectedTaskIndex.set(targetArr.length - 1);
      this.#touchDoc();
    } else if (this.#dragKind === 'move-task' && this.#dragSourceNodeKey && this.#dragSourceHook) {
      const srcArr = this.#getHookArray(this.#dragSourceNodeKey, this.#dragSourceHook);
      if (srcArr && this.#dragSourceIndex >= 0 && this.#dragSourceIndex < srcArr.length) {
        const [moving] = srcArr.splice(this.#dragSourceIndex, 1);
        if (moving) {
          targetArr.push(moving);
          this.selectedNodeKey.set(targetNodeKey);
          this.selectedTransitionIndex.set(-1);
          this.selectedTaskHook.set(targetHook);
          this.selectedTaskIndex.set(targetArr.length - 1);
          this.#touchDoc();
        }
      }
    }

    this.#clearDragState();
  }

  protected onDropOntoTransitionBadge(event: DragEvent, stateKey: string, transitionIndex: number): void {
    event.preventDefault();
    this.hoverDropKey.set(null);
    const stObj = this.states().find(s => s['key'] === stateKey);
    if (!stObj) {
      this.#clearDragState();
      return;
    }

    const trs = this.getTransitions(stObj);
    const trObj = trs[transitionIndex];
    if (!trObj) {
      this.#clearDragState();
      return;
    }

    const onExec = this.#ensureTasksArray(trObj, 'onExecute');
    if (this.#dragKind === 'palette-task' && this.#dragTaskType) {
      onExec.push(this.#createTaskNode(this.#dragTaskType));
      this.selectTransitionTask(stateKey, transitionIndex, onExec.length - 1);
      this.#touchDoc();
    } else if (this.#dragKind === 'move-task' && this.#dragSourceNodeKey && this.#dragSourceHook) {
      const srcArr = this.#getHookArray(this.#dragSourceNodeKey, this.#dragSourceHook);
      if (srcArr && this.#dragSourceIndex >= 0 && this.#dragSourceIndex < srcArr.length) {
        const [moving] = srcArr.splice(this.#dragSourceIndex, 1);
        if (moving) {
          onExec.push(moving);
          this.selectTransitionTask(stateKey, transitionIndex, onExec.length - 1);
          this.#touchDoc();
        }
      }
    }

    this.#clearDragState();
  }

  #completeConnection(fromNodeKey: string, targetStateKey: string): void {
    if (fromNodeKey === START_NODE_KEY) {
      this.startNode()['target'] = targetStateKey;
      this.selectStart();
      this.#touchDoc();
      return;
    }

    const fromState = this.states().find(s => s['key'] === fromNodeKey);
    if (!fromState || this.getString(fromState, 'type') === 'Finish') {
      return;
    }

    const trs = this.#ensureTasksArray(fromState, 'transitions');
    const actualTarget = fromNodeKey === targetStateKey ? '$self' : targetStateKey;
    const newIndex = trs.length;
    trs.push({
      key: `to-${targetStateKey}-${newIndex + 1}`,
      title: actualTarget === '$self' ? 'Güncelle' : `Geçiş: ${targetStateKey}`,
      target: actualTarget,
      trigger: 'Manual',
    });
    this.selectedNodeKeys.set(new Set());
    this.selectedEdgeIds.set(new Set([`${fromNodeKey}:${newIndex}`]));
    this.selectTransition(fromNodeKey, newIndex);
    this.#touchDoc();
  }

  protected clearHoverDrop(key: string): void {
    if (this.hoverDropKey() === key) {
      this.hoverDropKey.set(null);
    }
  }

  #clearDragState(): void {
    this.#dragKind = null;
    this.#dragStateType = '';
    this.#dragStateSubType = '';
    this.#dragStateTitle = '';
    this.#dragTaskType = '';
    this.#dragSourceNodeKey = '';
    this.#dragSourceHook = '';
    this.#dragSourceIndex = -1;
  }

  #clientToBoardPoint(clientX: number, clientY: number): NodePoint {
    const el = this.boardEl()?.nativeElement;
    if (el) {
      const rect = el.getBoundingClientRect();
      return {
        x: Math.max(0, (clientX - rect.left) / this.zoom()),
        y: Math.max(0, (clientY - rect.top) / this.zoom()),
      };
    }

    return { x: 200, y: 160 };
  }

  #computeBoardDropPoint(event: DragEvent): NodePoint {
    const pt = this.#clientToBoardPoint(event.clientX, event.clientY);
    return {
      x: Math.max(24, snap(pt.x - 110)),
      y: Math.max(24, snap(pt.y - 24)),
    };
  }

  #createStateOnBoard(type: string, subType: string, title: string, x: number, y: number): string {
    const d = this.doc();
    if (!Array.isArray(d['states'])) {
      d['states'] = [];
    }

    const arr = d['states'] as WorkflowData[];
    let idx = arr.length + 1;
    let key = `state-${idx}`;
    while (arr.some(s => s['key'] === key)) {
      idx++;
      key = `state-${idx}`;
    }

    const created: WorkflowData = {
      key,
      title,
      type,
      subType,
      step: idx,
    };
    if (type === 'SubFlow') {
      created['subFlow'] = {
        workflow: this.otherWorkflows()[0]?.key ?? '',
        into: 'subFlowResult',
        forwardTransitions: true,
      };
    }
    if (type !== 'Finish') {
      created['onEntry'] = [];
      created['transitions'] = [];
    }

    arr.push(created);
    this.#setNodePos(key, x, y);
    this.selectSingleState(key);
    this.#touchDoc();
    return key;
  }

  protected addStateAtDefaultPos(type: string, subType: string, title: string): void {
    const base = this.getNodePos(this.selectedNodeKey());
    this.#createStateOnBoard(type, subType, title, snap(base.x + 340), snap(base.y));
  }

  protected quickAddTaskToSelection(taskType: string): void {
    const targetNode = this.selectedNodeKey();
    const hook = targetNode === START_NODE_KEY ? 'onExecute' : 'onEntry';
    const arr = this.#getHookArray(targetNode, hook);
    if (!arr) {
      return;
    }

    arr.push(this.#createTaskNode(taskType));
    this.selectedTaskHook.set(hook);
    this.selectedTransitionIndex.set(-1);
    this.selectedTaskIndex.set(arr.length - 1);
    this.#touchDoc();
  }

  #createTaskNode(taskType: string): WorkflowData {
    const spec = this.findTaskSpec(taskType);
    const cfg = spec?.template ? structuredClone(spec.template) : {};
    const suffix = Math.random().toString(36).slice(2, 6);
    return {
      key: `${taskType}-${suffix}`,
      task: taskType,
      config: cfg,
    };
  }

  // --- 2D Layout & SVG Bezier Geometry ---

  #ensureNodePositions(forceAutoLayout: boolean): void {
    const d = this.doc();
    const layoutObj = d['x-layout'] as Record<string, { x?: number; y?: number }> | undefined;
    if (!forceAutoLayout && layoutObj && typeof layoutObj === 'object') {
      const map: Record<string, NodePoint> = {};
      for (const [k, v] of Object.entries(layoutObj)) {
        if (v && typeof v === 'object') {
          map[k] = { x: Number(v.x ?? 60), y: Number(v.y ?? 160) };
        }
      }

      if (map[START_NODE_KEY]) {
        this.#nodePositions.set(map);
        return;
      }
    }

    this.#computeDagAutoLayout();
  }

  #computeDagAutoLayout(): void {
    const map: Record<string, NodePoint> = {
      [START_NODE_KEY]: { x: 48, y: 96 },
    };
    const sts = this.states();
    const startTarget = this.getString(this.startNode(), 'target');
    const layers = new Map<string, number>();
    const queue: string[] = [];

    if (startTarget && sts.some(s => s['key'] === startTarget)) {
      layers.set(startTarget, 0);
      queue.push(startTarget);
    }

    while (queue.length > 0) {
      const cur = queue.shift()!;
      const curLayer = layers.get(cur) ?? 0;
      const stObj = sts.find(s => s['key'] === cur);
      if (stObj) {
        for (const tr of this.getTransitions(stObj)) {
          const tgt = this.getString(tr, 'target');
          if (tgt && tgt !== '$self' && !layers.has(tgt) && sts.some(s => s['key'] === tgt)) {
            layers.set(tgt, curLayer + 1);
            queue.push(tgt);
          }
        }
      }
    }

    let maxLayer = Math.max(0, ...Array.from(layers.values()));
    for (const st of sts) {
      const k = this.getString(st, 'key');
      if (k && !layers.has(k)) {
        maxLayer++;
        layers.set(k, maxLayer);
      }
    }

    const byLayer = new Map<number, string[]>();
    for (const [k, col] of layers.entries()) {
      const list = byLayer.get(col) ?? [];
      list.push(k);
      byLayer.set(col, list);
    }

    const layoutJson: Record<string, { x: number; y: number }> = {
      [START_NODE_KEY]: { x: 48, y: 96 },
    };
    for (const [col, keys] of byLayer.entries()) {
      const x = 352 + col * 496;
      let curY = 80;
      for (const k of keys) {
        const pt = { x: snap(x), y: snap(curY) };
        map[k] = pt;
        layoutJson[k] = pt;
        const stObj = sts.find(s => s['key'] === k);
        curY += this.#estimateStateNodeHeight(stObj) + 48;
      }
    }

    this.#nodePositions.set(map);
    this.doc()['x-layout'] = layoutJson;
  }

  #estimateStateNodeHeight(stateObj: WorkflowData | undefined): number {
    if (!stateObj) {
      return 220;
    }

    const onEntryCount = this.getTasks(stateObj, 'onEntry').length;
    const isFinish = this.getString(stateObj, 'type') === 'Finish';
    const onExitCount = isFinish ? 0 : this.getTasks(stateObj, 'onExit').length;
    const h = 150 + Math.max(1, onEntryCount) * 32 + (isFinish ? 0 : 52 + Math.max(1, onExitCount) * 32);
    return Math.max(210, h);
  }

  protected getNodePos(nodeKey: string): NodePoint {
    return this.#nodePositions()[nodeKey] ?? { x: 80, y: 160 };
  }

  #setNodePos(nodeKey: string, x: number, y: number): void {
    const pt = { x: snap(x), y: snap(y) };
    this.#nodePositions.update(prev => ({ ...prev, [nodeKey]: pt }));
    const d = this.doc();
    if (!d['x-layout'] || typeof d['x-layout'] !== 'object' || Array.isArray(d['x-layout'])) {
      d['x-layout'] = {};
    }

    (d['x-layout'] as Record<string, unknown>)[nodeKey] = pt;
  }

  #computeEdgeGeometry(
    fromKey: string,
    targetKey: string,
    parallelIndex: number,
    parallelTotal: number,
    outIndex = 0,
    outTotal = 1,
  ): EdgeGeometry {
    const fromPos = this.getNodePos(fromKey);
    const fromWidth = fromKey === START_NODE_KEY ? START_NODE_WIDTH : STATE_NODE_WIDTH;
    const x1 = fromPos.x + fromWidth;
    const y1 = fromPos.y + PORT_OFFSET_Y;

    if (fromKey === targetKey) {
      const loopHeight = 56 + parallelIndex * 24;
      const sx = fromPos.x + fromWidth - 36;
      const sy = fromPos.y;
      const ex = fromPos.x + fromWidth;
      const ey = fromPos.y + PORT_OFFSET_Y;
      const path = `M ${sx} ${sy} C ${sx} ${sy - loopHeight}, ${ex + 54} ${sy - loopHeight}, ${ex} ${ey}`;
      return { path, midX: sx + 22, midY: sy - loopHeight * 0.72 };
    }

    const toPos = this.getNodePos(targetKey);
    const x2 = toPos.x;
    const y2 = toPos.y + PORT_OFFSET_Y;
    const spread = (parallelIndex - (parallelTotal - 1) / 2) * 38;
    const dx = Math.max(90, Math.abs(x2 - x1) * 0.45);
    const cx1 = x1 + dx;
    const cy1 = y1 + spread;
    const cx2 = x2 - dx;
    const cy2 = y2 + spread;

    const path = `M ${x1} ${y1} C ${cx1} ${cy1}, ${cx2} ${cy2}, ${x2} ${y2}`;
    const t = outTotal <= 1 ? 0.5 : 0.34 + (outIndex / (outTotal - 1)) * 0.32;
    const u = 1 - t;
    const midX = u * u * u * x1 + 3 * u * u * t * cx1 + 3 * u * t * t * cx2 + t * t * t * x2;
    const midY = u * u * u * y1 + 3 * u * u * t * cy1 + 3 * u * t * t * cy2 + t * t * t * y2;
    return { path, midX, midY };
  }

  // --- Selection & Inspector Helpers ---

  #getHookArray(nodeKey: string, hook: string): WorkflowData[] | null {
    if (nodeKey === START_NODE_KEY) {
      return this.#ensureTasksArray(this.startNode(), 'onExecute');
    }

    const stObj = this.states().find(s => s['key'] === nodeKey);
    if (!stObj) {
      return null;
    }

    if (hook === 'onEntry' || hook === 'onExit') {
      return this.#ensureTasksArray(stObj, hook);
    }

    if (hook.startsWith('transition:')) {
      const trIdx = Number(hook.slice('transition:'.length));
      const trs = this.getTransitions(stObj);
      const trObj = trs[trIdx];
      return trObj ? this.#ensureTasksArray(trObj, 'onExecute') : null;
    }

    return null;
  }

  protected selectStart(): void {
    this.selectedNodeKeys.set(new Set([START_NODE_KEY]));
    this.selectedEdgeIds.set(new Set());
    this.selectedNodeKey.set(START_NODE_KEY);
    this.selectedTaskIndex.set(-1);
    this.selectedTransitionIndex.set(-1);
  }

  protected selectStartTask(idx: number): void {
    this.selectedNodeKeys.set(new Set());
    this.selectedEdgeIds.set(new Set());
    this.selectedNodeKey.set(START_NODE_KEY);
    this.selectedTaskHook.set('onExecute');
    this.selectedTaskIndex.set(idx);
    this.selectedTransitionIndex.set(-1);
  }

  protected selectStateTask(stateKey: string, hook: string, idx: number): void {
    this.selectedNodeKeys.set(new Set());
    this.selectedEdgeIds.set(new Set());
    this.selectedNodeKey.set(stateKey);
    this.selectedTaskHook.set(hook);
    this.selectedTaskIndex.set(idx);
    this.selectedTransitionIndex.set(-1);
  }

  protected selectTransition(stateKey: string, trIdx: number): void {
    this.selectedNodeKey.set(stateKey);
    this.selectedTransitionIndex.set(trIdx);
    this.selectedTaskIndex.set(-1);
  }

  protected selectTransitionTask(stateKey: string, trIdx: number, taskIdx: number): void {
    this.selectedNodeKeys.set(new Set());
    this.selectedEdgeIds.set(new Set());
    this.selectedNodeKey.set(stateKey);
    this.selectedTransitionIndex.set(-1);
    this.selectedTaskHook.set(`transition:${trIdx}`);
    this.selectedTaskIndex.set(taskIdx);
  }

  protected moveSelectedTask(delta: number): void {
    const arr = this.#getHookArray(this.selectedNodeKey(), this.selectedTaskHook());
    const idx = this.selectedTaskIndex();
    if (!arr || idx < 0) {
      return;
    }

    const next = idx + delta;
    if (next < 0 || next >= arr.length) {
      return;
    }

    const [item] = arr.splice(idx, 1);
    if (item) {
      arr.splice(next, 0, item);
      this.selectedTaskIndex.set(next);
      this.#touchDoc();
    }
  }

  protected deleteSelectedTask(): void {
    const arr = this.#getHookArray(this.selectedNodeKey(), this.selectedTaskHook());
    const idx = this.selectedTaskIndex();
    if (!arr || idx < 0 || idx >= arr.length) {
      return;
    }

    arr.splice(idx, 1);
    this.selectedTaskIndex.set(-1);
    this.#touchDoc();
  }

  protected deleteSelectedTransition(): void {
    const stObj = this.selectedState();
    const idx = this.selectedTransitionIndex();
    if (!stObj || idx < 0) {
      return;
    }

    const trs = this.#ensureTasksArray(stObj, 'transitions');
    if (idx < trs.length) {
      trs.splice(idx, 1);
      this.selectedTransitionIndex.set(-1);
      this.#touchDoc();
    }
  }

  protected removeState(stateKey: string): void {
    const d = this.doc();
    if (!Array.isArray(d['states'])) {
      return;
    }

    d['states'] = (d['states'] as WorkflowData[]).filter(s => s['key'] !== stateKey);
    this.#nodePositions.update(prev => {
      const copy = { ...prev };
      delete copy[stateKey];
      return copy;
    });
    if (d['x-layout'] && typeof d['x-layout'] === 'object') {
      delete (d['x-layout'] as Record<string, unknown>)[stateKey];
    }

    const nextNodes = new Set(this.selectedNodeKeys());
    nextNodes.delete(stateKey);
    this.selectedNodeKeys.set(nextNodes);
    this.selectedNodeKey.set(START_NODE_KEY);
    this.#touchDoc();
  }

  protected renameState(stateObj: WorkflowData, newKey: string): void {
    const trimmed = newKey.trim();
    if (!trimmed) {
      return;
    }

    const oldKey = this.getString(stateObj, 'key');
    stateObj['key'] = trimmed;

    if (oldKey) {
      const oldPos = this.getNodePos(oldKey);
      this.#setNodePos(trimmed, oldPos.x, oldPos.y);
      const start = this.startNode();
      if (start['target'] === oldKey) {
        start['target'] = trimmed;
      }

      for (const st of this.states()) {
        for (const tr of this.getTransitions(st)) {
          if (tr['target'] === oldKey) {
            tr['target'] = trimmed;
          }
        }
      }
    }

    this.selectedNodeKey.set(trimmed);
    this.#touchDoc();
  }

  protected addTransition(stateObj: WorkflowData): void {
    const trs = this.#ensureTasksArray(stateObj, 'transitions');
    const firstKey = this.states().map(s => this.getString(s, 'key')).find(k => k.length > 0) ?? '$self';
    trs.push({
      key: `transition-${trs.length + 1}`,
      title: 'Devam Et',
      target: firstKey,
      trigger: 'Manual',
    });
    this.selectedTransitionIndex.set(trs.length - 1);
    this.selectedTaskIndex.set(-1);
    this.#touchDoc();
  }

  protected findTaskSpec(taskType: string): WorkflowTaskSpecModel | undefined {
    const lower = taskType.toLowerCase();
    return this.taskSpecs().find(s => s.name.toLowerCase() === lower);
  }

  protected changeTaskType(taskObj: WorkflowData, newType: string): void {
    if (!newType) {
      return;
    }

    taskObj['task'] = newType;
    const spec = this.findTaskSpec(newType);
    taskObj['config'] = spec?.template ? structuredClone(spec.template) : {};
    this.#touchDoc();
  }

  protected getConfig(taskObj: WorkflowData): WorkflowData {
    if (!taskObj['config'] || typeof taskObj['config'] !== 'object' || Array.isArray(taskObj['config'])) {
      taskObj['config'] = {};
    }

    return taskObj['config'] as WorkflowData;
  }

  protected getErrorAction(taskObj: WorkflowData): string {
    const err = taskObj['onError'] as WorkflowData | undefined;
    return err ? this.getString(err, 'action') || 'Fault' : 'Fault';
  }

  protected setErrorAction(taskObj: WorkflowData, action: string): void {
    if (!action || action === 'Fault' || action === 'Fail') {
      const existing = taskObj['onError'] as WorkflowData | undefined;
      if (existing) {
        delete existing['action'];
        delete existing['transition'];
        if (Object.keys(existing).length === 0 || this.getInt(existing, 'maxAttempts', 1) <= 1) {
          delete taskObj['onError'];
        }
      }
    } else {
      const err = (taskObj['onError'] as WorkflowData | undefined) ?? {};
      err['action'] = action;
      if (action !== 'Transition') {
        delete err['transition'];
      }

      taskObj['onError'] = err;
    }

    this.#touchDoc();
  }

  protected setErrorAttempts(taskObj: WorkflowData, raw: string): void {
    const parsed = Number.parseInt(raw, 10);
    const attempts = Number.isFinite(parsed) ? Math.max(1, parsed) : 1;
    const existing = taskObj['onError'] as WorkflowData | undefined;
    if (attempts <= 1 && (!existing || (this.getString(existing, 'action') || 'Fault') === 'Fault')) {
      delete taskObj['onError'];
    } else {
      const err = existing ?? {};
      err['maxAttempts'] = attempts;
      if (!err['delay']) {
        err['delay'] = '00:00:05';
      }
      taskObj['onError'] = err;
    }

    this.#touchDoc();
  }

  protected getErrorInt(taskObj: WorkflowData, prop: string, fallback: number): number {
    const err = taskObj['onError'] as WorkflowData | undefined;
    return err ? this.getInt(err, prop, fallback) : fallback;
  }

  protected getErrorString(taskObj: WorkflowData, prop: string): string {
    const err = taskObj['onError'] as WorkflowData | undefined;
    return err ? this.getString(err, prop) : '';
  }

  protected setErrorString(taskObj: WorkflowData, prop: string, val: string): void {
    const err = (taskObj['onError'] as WorkflowData | undefined) ?? {};
    err[prop] = val.trim();
    taskObj['onError'] = err;
    this.#touchDoc();
  }

  protected getParamBool(cfg: WorkflowData, name: string, defaultVal?: string | null): boolean {
    if (typeof cfg[name] === 'boolean') {
      return cfg[name] as boolean;
    }

    return defaultVal === 'true';
  }

  protected setParamBool(cfg: WorkflowData, name: string, checked: boolean): void {
    cfg[name] = checked;
    this.#touchDoc();
  }

  protected getJsonParamText(cfg: WorkflowData, prop: string): string {
    const val = cfg[prop];
    if (val === undefined || val === null) {
      return '';
    }

    return typeof val === 'string' ? val : JSON.stringify(val, null, 2);
  }

  protected setJsonParam(cfg: WorkflowData, prop: string, raw: string): void {
    const trimmed = raw.trim();
    if (!trimmed) {
      delete cfg[prop];
    } else {
      try {
        cfg[prop] = JSON.parse(trimmed);
      } catch {
        cfg[prop] = trimmed;
      }
    }

    this.#touchDoc();
  }

  protected addSchemaField(): void {
    const name = this.newSchemaFieldName().trim();
    if (!name) {
      return;
    }

    const d = this.doc();
    if (!d['dataSchema'] || typeof d['dataSchema'] !== 'object') {
      d['dataSchema'] = { type: 'object', properties: {} };
    }

    const schema = d['dataSchema'] as WorkflowData;
    schema['type'] = 'object';
    if (!schema['properties'] || typeof schema['properties'] !== 'object') {
      schema['properties'] = {};
    }

    (schema['properties'] as WorkflowData)[name] = {
      type: 'string',
      title: name,
      'x-search': true,
      'x-list': true,
    };
    this.newSchemaFieldName.set('');
    this.#touchDoc();
  }

  protected removeSchemaField(name: string): void {
    const schema = this.doc()['dataSchema'] as WorkflowData | undefined;
    if (!schema) {
      return;
    }

    const props = schema['properties'] as WorkflowData | undefined;
    if (props) {
      delete props[name];
    }

    if (Array.isArray(schema['required'])) {
      schema['required'] = (schema['required'] as unknown[]).filter(x => x !== name);
    }

    this.#touchDoc();
  }

  protected isRequiredField(name: string): boolean {
    const schema = this.doc()['dataSchema'] as WorkflowData | undefined;
    return Array.isArray(schema?.['required']) && (schema['required'] as unknown[]).includes(name);
  }

  protected toggleRequiredField(name: string, required: boolean): void {
    const d = this.doc();
    if (!d['dataSchema'] || typeof d['dataSchema'] !== 'object') {
      d['dataSchema'] = { type: 'object', properties: {} };
    }

    const schema = d['dataSchema'] as WorkflowData;
    const req = Array.isArray(schema['required']) ? (schema['required'] as unknown[]).filter(x => x !== name) : [];
    if (required) {
      req.push(name);
    }

    schema['required'] = req;
    this.#touchDoc();
  }

  protected onRawJsonInput(raw: string): void {
    this.json.set(raw);
    this.#tryParseRawJson();
  }

  protected formatJson(): void {
    if (this.#tryParseRawJson()) {
      this.#syncJsonFromDoc();
    }
  }

  protected async validate(): Promise<void> {
    this.errors.set([]);
    this.validMessage.set(null);
    if (!this.#tryParseRawJson()) {
      return;
    }

    const resp = await this.#client.validateDefinition(this.doc());
    if (!isSuccess(resp) || !resp.data) {
      this.errors.set(resp.messageList);
      return;
    }

    if (resp.data.isValid) {
      this.validMessage.set('✔ İş akışı tanımı, geçiş hedefleri ve tüm C# ifadeleri geçerli.');
    } else {
      this.errors.set(resp.data.errors);
    }
  }

  protected async save(): Promise<void> {
    this.errors.set([]);
    this.validMessage.set(null);
    if (!this.#tryParseRawJson()) {
      return;
    }

    const key = this.getString(this.doc(), 'key').trim();
    if (!key) {
      this.errors.set(['İş akışı anahtarı (key) zorunludur.']);
      return;
    }

    this.busy.set(true);
    try {
      const resp = await this.#client.saveDefinition(key, this.doc());
      if (!isSuccess(resp)) {
        this.errors.set(resp.messageList);
        return;
      }

      this.selectedWorkflowKey.set(key);
      this.validMessage.set(`✔ '${key}' iş akışı kaydedildi ve çalışma zamanında devreye alındı.`);
      this.#snack.open(`'${key}' iş akışı kaydedildi.`, undefined, { duration: 2500 });

      const listResp = await this.#client.listWorkflows();
      if (isSuccess(listResp) && listResp.data) {
        this.workflows.set(listResp.data);
      }

      this.changed.emit();
    } finally {
      this.busy.set(false);
    }
  }

  // --- Value Accessors & Formatting ---

  protected getTasks(holder: WorkflowData, hook: string): WorkflowData[] {
    const raw = holder[hook];
    return Array.isArray(raw) ? (raw.filter(x => x && typeof x === 'object') as WorkflowData[]) : [];
  }

  #ensureTasksArray(holder: WorkflowData, hook: string): WorkflowData[] {
    if (!Array.isArray(holder[hook])) {
      holder[hook] = [];
    }

    return holder[hook] as WorkflowData[];
  }

  protected getTransitions(stateObj: WorkflowData): WorkflowData[] {
    return this.getTasks(stateObj, 'transitions');
  }

  protected getString(obj: WorkflowData | null | undefined, prop: string): string {
    if (!obj) {
      return '';
    }

    const val = obj[prop];
    return typeof val === 'string' ? val : '';
  }

  protected setString(obj: WorkflowData, prop: string, value: string): void {
    const trimmed = value.trim();
    if (!trimmed) {
      delete obj[prop];
    } else {
      obj[prop] = trimmed;
    }

    this.#touchDoc();
  }

  protected getNestedString(obj: WorkflowData, parentProp: string, childProp: string): string {
    const p = obj[parentProp] as WorkflowData | undefined;
    return p && typeof p === 'object' ? this.getString(p, childProp) : '';
  }

  protected setNestedString(obj: WorkflowData, parentProp: string, childProp: string, value: string): void {
    const trimmed = value.trim();
    if (!trimmed) {
      const p = obj[parentProp] as WorkflowData | undefined;
      if (p && typeof p === 'object') {
        delete p[childProp];
        if (Object.keys(p).length === 0) {
          delete obj[parentProp];
        }
      }
    } else {
      if (!obj[parentProp] || typeof obj[parentProp] !== 'object') {
        obj[parentProp] = {};
      }

      (obj[parentProp] as WorkflowData)[childProp] = trimmed;
    }

    this.#touchDoc();
  }

  protected getInt(obj: WorkflowData, prop: string, fallback: number): number {
    const val = obj[prop];
    return typeof val === 'number' && Number.isFinite(val) ? val : fallback;
  }

  protected setIntNullable(obj: WorkflowData, prop: string, raw: string): void {
    const parsed = Number.parseInt(raw, 10);
    if (Number.isFinite(parsed)) {
      obj[prop] = parsed;
    } else {
      delete obj[prop];
    }

    this.#touchDoc();
  }

  protected setBool(obj: WorkflowData, prop: string, checked: boolean): void {
    if (checked) {
      obj[prop] = true;
    } else {
      delete obj[prop];
    }

    this.#touchDoc();
  }

  protected setExplicitBool(obj: WorkflowData, prop: string, checked: boolean): void {
    obj[prop] = checked;
    this.#touchDoc();
  }

  protected getCsvArray(obj: WorkflowData, prop: string): string {
    const arr = obj[prop];
    return Array.isArray(arr) ? arr.filter(x => typeof x === 'string').join(', ') : '';
  }

  protected setCsvArray(obj: WorkflowData, prop: string, csv: string): void {
    const items = csv
      .split(',')
      .map(s => s.trim())
      .filter(s => s.length > 0);
    if (items.length === 0) {
      delete obj[prop];
    } else {
      obj[prop] = items;
    }

    this.#touchDoc();
  }

  protected getAccessMode(): string {
    return this.getNestedString(this.doc(), 'instanceAccess', 'mode') || 'all';
  }

  protected setAccessMode(mode: string): void {
    const d = this.doc();
    if (!mode || mode === 'all') {
      delete d['instanceAccess'];
    } else {
      d['instanceAccess'] = { mode };
    }

    this.#touchDoc();
  }

  protected getStartNodeClass(): string {
    let cls = 'middt-wf-board-node middt-wf-board-node-start';
    if (this.selectedNodeKeys().has(START_NODE_KEY)) {
      cls += ' middt-wf-board-node-multi';
    } else if (this.selectedNodeKey() === START_NODE_KEY && this.selectedTransitionIndex() < 0) {
      cls += ' middt-wf-board-node-selected';
    }

    return cls;
  }

  protected getBoardNodeClass(stateKey: string, stateType: string, stateSubType: string): string {
    let cls = 'middt-wf-board-node';
    if (stateType === 'Finish') {
      cls +=
        stateSubType === 'Error' || stateSubType === 'Cancelled' || stateSubType === 'Timeout'
          ? ' middt-wf-board-node-finish-error'
          : ' middt-wf-board-node-finish-success';
    }

    if (this.hoverConnectTargetKey() === stateKey) {
      cls += ' middt-wf-board-node-connect-target';
    } else if (this.selectedNodeKeys().has(stateKey) && this.selectedNodeKeys().size > 1) {
      cls += ' middt-wf-board-node-multi';
    } else if (this.selectedNodeKey() === stateKey || this.selectedNodeKeys().has(stateKey)) {
      cls += ' middt-wf-board-node-selected';
    }

    return cls;
  }

  protected stateBadgeClass(stateType: string, stateSubType: string): string {
    if (stateType === 'Finish' && stateSubType === 'Success') return 'middt-ui-badge-success';
    if (stateType === 'Finish' && (stateSubType === 'Error' || stateSubType === 'Timeout'))
      return 'middt-ui-badge-danger';
    if (stateType === 'Finish' && stateSubType === 'Cancelled') return 'middt-ui-badge-warning';
    if (stateSubType === 'Human') return 'middt-ui-badge-primary';
    return 'middt-ui-badge-info';
  }

  protected triggerIcon(trigger: string): string {
    if (trigger === 'Automatic') return '⚡';
    if (trigger === 'Scheduled') return '⏱️';
    if (trigger === 'Event') return '📨';
    return '👆';
  }

  protected taskSummary(taskObj: WorkflowData): string {
    const type = this.getString(taskObj, 'task') || 'set';
    const key = this.getString(taskObj, 'key') || type;
    return `${key} (${type})`;
  }

  protected taskBadges(taskObj: WorkflowData): string {
    const parts: string[] = [];
    if (taskObj['background'] === true) parts.push('bg');
    if (this.getString(taskObj, 'when')) parts.push('when');
    const act = this.getErrorAction(taskObj);
    if (act !== 'Fault' && act !== 'Fail') parts.push(act);
    if (this.getErrorInt(taskObj, 'maxAttempts', 1) > 1) parts.push(`x${this.getErrorInt(taskObj, 'maxAttempts', 1)}`);
    return parts.join('·');
  }

  #touchDoc(): void {
    this.doc.set({ ...this.doc() });
    this.#syncJsonFromDoc();
  }

  #syncJsonFromDoc(): void {
    this.json.set(JSON.stringify(this.doc(), null, 2));
    this.validMessage.set(null);
  }

  #tryParseRawJson(): boolean {
    try {
      const parsed = JSON.parse(this.json());
      if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) {
        this.doc.set(parsed as WorkflowData);
        this.#ensureNodePositions(false);
        this.errors.set([]);
        return true;
      }

      this.errors.set(['İş akışı tanımı bir JSON nesnesi ({ ... }) olmalıdır.']);
      return false;
    } catch (e) {
      this.errors.set([`JSON sözdizimi hatası: ${e instanceof Error ? e.message : String(e)}`]);
      return false;
    }
  }
}

function snap(v: number): number {
  return Math.round(v / 16) * 16;
}

function createStarterWorkflow(): WorkflowData {
  return {
    key: 'new-workflow',
    version: 1,
    title: 'Yeni İş Akışı',
    icon: 'account_tree',
    history: 'Full',
    dataSchema: {
      type: 'object',
      required: ['title'],
      properties: {
        title: { type: 'string', title: 'Başlık', 'x-search': true, 'x-list': true },
        amount: { type: 'number', title: 'Tutar', 'x-search': true, 'x-list': true },
      },
    },
    start: {
      key: 'start',
      target: 'review',
      input: {
        schema: {
          type: 'object',
          required: ['title'],
          properties: {
            title: { type: 'string', title: 'Başlık' },
            amount: { type: 'number', title: 'Tutar' },
          },
        },
      },
      onExecute: [
        {
          key: 'assign-key',
          task: 'setKey',
          config: {
            key: 'WF-{{ now.ToString("yyyyMMdd") }}',
            ensureUnique: true,
          },
        },
      ],
    },
    states: [
      {
        key: 'review',
        title: 'İnceleme & Onay',
        type: 'Intermediate',
        subType: 'Human',
        step: 1,
        onEntry: [],
        transitions: [
          {
            key: 'approve',
            title: 'Onayla',
            target: 'approved',
            trigger: 'Manual',
            style: 'success',
            icon: 'check_circle',
          },
          {
            key: 'reject',
            title: 'Reddet',
            target: 'rejected',
            trigger: 'Manual',
            style: 'danger',
            icon: 'cancel',
          },
        ],
      },
      {
        key: 'approved',
        title: 'Onaylandı',
        type: 'Finish',
        subType: 'Success',
        step: 2,
      },
      {
        key: 'rejected',
        title: 'Reddedildi',
        type: 'Finish',
        subType: 'Error',
        step: 2,
      },
    ],
  };
}
