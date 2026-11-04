import { useEffect, useRef, useState } from 'react';
import { Center, Text } from '@mantine/core';
import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import type { ViewerSceneView } from '../../../api/types';
import { type Box, frameForHit, type Isolation, isVisible, roleColor, type Vec3, type ViewerMesh } from '../../../lib/viewer';
import { dragFace, elementBox, type Handle, handles } from './sectionGizmo';
import { createSectionPlanes, updateSectionPlanes } from './sectionPlanes';

export interface ClashViewerProps {
  scene: ViewerSceneView;
  /** Сетки по индексу элемента; пока не пришли — элементы показываются габаритами. */
  meshes?: Map<number, ViewerMesh>;
  isolation: Isolation;
  /** Секущий куб в мм от точки коллизии; null — выключен. */
  sectionBox: Box | null;
  selected: number | null;
  onPick: (index: number | null) => void;
  /** Секущий куб изменён в сцене (перетаскиванием грани). */
  onSectionChange?: (box: Box) => void;
  /** Элемент, на который ставится кадр (образец набора); без него — на все элементы, кроме окружения. */
  frame?: number | null;
}

interface Stage {
  renderer: THREE.WebGLRenderer;
  camera: THREE.PerspectiveCamera;
  controls: OrbitControls;
  world: THREE.Scene;
  group: THREE.Group;
  outline: THREE.Group;
  /** Секущий куб в сцене: полупрозрачная коробка, контур и ручки граней; строится один раз, куб его двигает и растягивает. */
  gizmo: THREE.Group;
  /** Шесть плоскостей куба: одни на всё время просмотрщика, у материалов — ссылка на этот массив. */
  planes: THREE.Plane[];
  /** Кадр в ближайшем requestAnimationFrame: сколько бы раз за кадр ни попросили, рисуется один раз. */
  render: () => void;
}

const Selected = '#fab005';

function dispose(object: THREE.Object3D) {
  object.traverse((child) => {
    const item = child as THREE.Mesh;
    item.geometry?.dispose();
    const material = item.material;
    if (Array.isArray(material)) material.forEach((m) => m.dispose());
    else material?.dispose();
  });
}

/** Плоскости отсечения материала: куб включён — общий массив, выключен — пусто. Перекомпиляция — только при включении и выключении. */
function clip(material: THREE.Material, planes: THREE.Plane[], on: boolean) {
  const wanted = on ? planes : [];
  if (material.clippingPlanes === wanted || (!on && !material.clippingPlanes?.length)) return;
  material.clippingPlanes = wanted;
  material.needsUpdate = true;
}

/** Секущий куб: коробка и контур единичного размера (растягиваются), шесть ручек (двигаются). */
function buildGizmo(gizmo: THREE.Group) {
  const geometry = new THREE.BoxGeometry(1, 1, 1);
  const shell = new THREE.Mesh(geometry, new THREE.MeshBasicMaterial({ color: Selected, transparent: true, opacity: 0.05, depthWrite: false }));
  const edges = new THREE.LineSegments(new THREE.EdgesGeometry(geometry), new THREE.LineBasicMaterial({ color: Selected }));
  shell.raycast = () => {};
  edges.raycast = () => {};
  shell.userData.frame = true;
  edges.userData.frame = true;
  gizmo.add(shell, edges);
  const knob = new THREE.SphereGeometry(1, 16, 12);
  for (let i = 0; i < 6; i++) {
    const mesh = new THREE.Mesh(knob, new THREE.MeshBasicMaterial({ color: Selected, depthTest: false }));
    mesh.renderOrder = 3;
    gizmo.add(mesh);
  }
  gizmo.visible = false;
}

function placeGizmo(gizmo: THREE.Group, box: Box | null) {
  gizmo.visible = !!box;
  if (!box) return;
  const size = [0, 1, 2].map((i) => Math.max(box.max[i] - box.min[i], 1)) as Vec3;
  const center = [0, 1, 2].map((i) => (box.min[i] + box.max[i]) / 2) as Vec3;
  const radius = Math.max(20, Math.hypot(...size) / 60);
  const knobs = handles(box);
  let k = 0;
  for (const child of gizmo.children) {
    if (child.userData.frame) {
      child.position.set(...center);
      child.scale.set(...size);
    } else {
      const handle = knobs[k++];
      child.position.set(...handle.position);
      child.scale.setScalar(radius);
      child.userData.handle = handle;
    }
  }
}

function inside(point: THREE.Vector3, box: Box | null): boolean {
  if (!box) return true;
  return [0, 1, 2].every((axis) => point.getComponent(axis) >= box.min[axis] && point.getComponent(axis) <= box.max[axis]);
}

/**
 * Сцена коллизии на three.js. Начало координат — точка коллизии, единицы — мм, ось Z вверх (как в Revit).
 * A и B — контрастные цвета, окружение полупрозрачное, элементы без сетки — каркасы габаритов.
 */
export default function ClashViewer({ scene, meshes, isolation, sectionBox, selected, onPick, onSectionChange, frame }: ClashViewerProps) {
  const container = useRef<HTMLDivElement>(null);
  const stage = useRef<Stage | null>(null);
  const pick = useRef(onPick);
  pick.current = onPick;
  const section = useRef(sectionBox);
  section.current = sectionBox;
  const changeSection = useRef(onSectionChange);
  changeSection.current = onSectionChange;
  const current = useRef(scene);
  current.current = scene;
  const [failed, setFailed] = useState(false);

  // Рендерер, камера и управление живут, пока открыт просмотрщик; сцена коллизии меняется ниже.
  useEffect(() => {
    const host = container.current;
    if (!host) return;
    let renderer: THREE.WebGLRenderer;
    try {
      renderer = new THREE.WebGLRenderer({ antialias: true });
    } catch {
      setFailed(true);
      return;
    }

    renderer.setPixelRatio(window.devicePixelRatio);
    renderer.localClippingEnabled = true;
    renderer.setClearColor('#f8f9fa');
    host.appendChild(renderer.domElement);

    const camera = new THREE.PerspectiveCamera(45, 1, 10, 1_000_000);
    camera.up.set(0, 0, 1);
    const world = new THREE.Scene();
    world.add(new THREE.AmbientLight('#ffffff', 1.2));
    const light = new THREE.DirectionalLight('#ffffff', 1.8);
    light.position.set(0.5, -0.3, 1);
    camera.add(light);
    world.add(camera);
    const group = new THREE.Group();
    const outline = new THREE.Group();
    const gizmo = new THREE.Group();
    world.add(group, outline, gizmo);

    buildGizmo(gizmo);
    const planes = createSectionPlanes();

    const controls = new OrbitControls(camera, renderer.domElement);
    let frameRequest = 0;
    const render = () => {
      if (frameRequest) return;
      frameRequest = requestAnimationFrame(() => {
        frameRequest = 0;
        renderer.render(world, camera);
      });
    };
    controls.addEventListener('change', render);
    stage.current = { renderer, camera, controls, world, group, outline, gizmo, planes, render };

    const resize = () => {
      const { clientWidth, clientHeight } = host;
      if (clientWidth === 0 || clientHeight === 0) return;
      renderer.setSize(clientWidth, clientHeight);
      camera.aspect = clientWidth / clientHeight;
      camera.updateProjectionMatrix();
      // Сразу, а не в следующем кадре: setSize очищает холст, иначе он на кадр мигнёт пустым.
      renderer.render(world, camera);
    };
    const observer = new ResizeObserver(resize);
    observer.observe(host);
    resize();

    const ray = (e: MouseEvent) => {
      const rect = renderer.domElement.getBoundingClientRect();
      const pointer = new THREE.Vector2(((e.clientX - rect.left) / rect.width) * 2 - 1, -((e.clientY - rect.top) / rect.height) * 2 + 1);
      const raycaster = new THREE.Raycaster();
      raycaster.params.Line = { threshold: camera.position.length() / 200 };
      raycaster.setFromCamera(pointer, camera);
      return raycaster;
    };
    const handleAt = (e: MouseEvent) => ray(e).intersectObjects(gizmo.children.filter((c) => c.userData.handle), false)[0];
    const elementAt = (e: MouseEvent) =>
      ray(e).intersectObjects(group.children.filter((c) => c.visible), false).find((i) => inside(i.point, section.current));

    // Ручка куба под мышью тянет свою грань (камера в это время не вращается); щелчок без перетаскивания — выбор элемента.
    let down: { x: number; y: number } | null = null;
    let drag: { handle: Handle; start: Vec3; box: Box } | null = null;
    let hovered: THREE.Object3D | undefined;
    const onDown = (e: PointerEvent) => {
      down = { x: e.clientX, y: e.clientY };
      const hit = section.current && handleAt(e);
      if (!hit || !section.current) return;
      drag = { handle: hit.object.userData.handle as Handle, start: hit.point.toArray() as Vec3, box: section.current };
      controls.enabled = false;
      renderer.domElement.setPointerCapture(e.pointerId);
    };
    const onMove = (e: PointerEvent) => {
      if (drag) {
        const r = ray(e).ray;
        changeSection.current?.(dragFace(drag.box, drag.handle, r.origin.toArray() as Vec3, r.direction.toArray() as Vec3, drag.start));
        return;
      }
      const hover = section.current ? handleAt(e)?.object : undefined;
      if (hover === hovered) return;
      hovered = hover;
      renderer.domElement.style.cursor = hover ? 'grab' : '';
      for (const child of gizmo.children)
        if (child.userData.handle) ((child as THREE.Mesh).material as THREE.MeshBasicMaterial).color.set(child === hover ? '#e8590c' : Selected);
      render();
    };
    const onUp = (e: PointerEvent) => {
      if (drag) {
        drag = null;
        controls.enabled = true;
        renderer.domElement.releasePointerCapture(e.pointerId);
        return;
      }
      if (!down || Math.hypot(e.clientX - down.x, e.clientY - down.y) > 4) return;
      const hit = elementAt(e);
      pick.current(hit ? (hit.object.userData.index as number) : null);
    };
    // Двойной щелчок по элементу — секущий куб по его габариту с запасом.
    const onDouble = (e: MouseEvent) => {
      const hit = elementAt(e);
      const element = hit ? current.current.elements[hit.object.userData.index as number] : undefined;
      if (element) changeSection.current?.(elementBox(element));
    };
    renderer.domElement.addEventListener('pointerdown', onDown);
    renderer.domElement.addEventListener('pointermove', onMove);
    renderer.domElement.addEventListener('pointerup', onUp);
    renderer.domElement.addEventListener('dblclick', onDouble);

    return () => {
      cancelAnimationFrame(frameRequest);
      observer.disconnect();
      renderer.domElement.removeEventListener('pointerdown', onDown);
      renderer.domElement.removeEventListener('pointermove', onMove);
      renderer.domElement.removeEventListener('pointerup', onUp);
      renderer.domElement.removeEventListener('dblclick', onDouble);
      controls.dispose();
      dispose(world);
      renderer.dispose();
      // Браузер держит ограниченное число контекстов WebGL (около 16): без этого после частых открытий 3D старые теряются.
      renderer.forceContextLoss();
      host.removeChild(renderer.domElement);
      stage.current = null;
    };
  }, []);

  // Объекты сцены: пересобираются, когда пришла другая коллизия или её сетки.
  useEffect(() => {
    const s = stage.current;
    if (!s) return;
    dispose(s.group);
    s.group.clear();
    for (const element of scene.elements) {
      const { color, opacity } = roleColor(element.role);
      const mesh = meshes?.get(element.index);
      let object: THREE.Object3D;
      if (mesh) {
        const geometry = new THREE.BufferGeometry();
        geometry.setAttribute('position', new THREE.BufferAttribute(mesh.positions, 3));
        geometry.setIndex(new THREE.BufferAttribute(mesh.indices, 1));
        geometry.computeVertexNormals();
        // Все материалы прозрачные, чтобы порядок задавал renderOrder: окружение (глубину не пишет) рисуется первым,
        // A и B — поверх него. Иначе в плотном узле сотни полупрозрачных слоёв закрывают пару целиком.
        const pair = element.role !== 'context';
        object = new THREE.Mesh(
          geometry,
          new THREE.MeshLambertMaterial({ color, opacity, transparent: true, depthWrite: pair, side: THREE.DoubleSide }),
        );
        object.renderOrder = pair ? 2 : 1;
      } else if (element.role === 'context') {
        // Окружение без сетки каркасом габарита не рисуется: десятки рамок заслоняют пару и ничего не объясняют.
        continue;
      } else {
        const size = element.max.map((v, i) => Math.max(v - element.min[i], 1)) as [number, number, number];
        object = new THREE.LineSegments(
          new THREE.EdgesGeometry(new THREE.BoxGeometry(...size)),
          new THREE.LineBasicMaterial({ color, opacity: Math.max(opacity, 0.6), transparent: true }),
        );
        object.position.set(...(element.min.map((v, i) => (v + element.max[i]) / 2) as [number, number, number]));
      }

      object.userData.index = element.index;
      s.group.add(object);
    }

    s.render();
  }, [scene, meshes]);

  // Камера — на коллизию (или на образец набора), когда открыта новая сцена (не при подгрузке сеток). У сцены набора без
  // образца окружения нет (радиус 0) — кадр по габариту набора.
  useEffect(() => {
    const s = stage.current;
    if (!s) return;
    const view = new THREE.Vector3(1, -1.2, 0.8).normalize();
    const hit = frame == null && scene.elements.some((e) => e.role === 'A' || e.role === 'B');
    let target = new THREE.Vector3(0, 0, 0);
    let distance: number;
    let extent: number;
    if (hit) {
      // Коллизия — кадр по месту прохода элементов друг через друга плюс 1,5 м, а не по их целым габаритам (длинная труба).
      const box = frameForHit(scene);
      target = new THREE.Vector3(...box.center);
      extent = box.radius;
      distance = box.radius / Math.sin(THREE.MathUtils.degToRad(s.camera.fov / 2));
    } else {
      const framed = frame != null && scene.elements[frame] ? [scene.elements[frame]] : scene.elements.filter((e) => e.role !== 'context');
      extent = Math.max(
        300,
        ...framed.flatMap((e) => [0, 1, 2].map((axis) => Math.max(Math.abs(e.min[axis]), Math.abs(e.max[axis])))),
      );
      distance = scene.radiusMm > 0 ? Math.min(extent * 4, scene.radiusMm * 1.5) : extent * 2.5;
    }
    s.camera.position.copy(target.clone().add(view.multiplyScalar(distance)));
    s.camera.near = Math.max(1, distance / 2000);
    s.camera.far = distance * 20 + Math.max(scene.radiusMm, extent) * 4;
    s.camera.updateProjectionMatrix();
    s.controls.target.copy(target);
    s.controls.update();
    s.render();
  }, [scene]); // eslint-disable-line react-hooks/exhaustive-deps

  // Куб двигается: меняются только константы общих плоскостей и положение гизмо — материалы и контур не трогаются.
  useEffect(() => {
    const s = stage.current;
    if (!s) return;
    if (sectionBox) updateSectionPlanes(s.planes, sectionBox);
    placeGizmo(s.gizmo, sectionBox);
    s.render();
  }, [sectionBox]);

  // Изоляция, включение куба и подсветка выбранного — без пересборки сеток; контур выбранного — только при смене выбора.
  const sectionOn = sectionBox !== null;
  useEffect(() => {
    const s = stage.current;
    if (!s) return;
    for (const object of s.group.children) {
      const element = scene.elements[object.userData.index as number];
      object.visible = element ? isVisible(element, isolation, selected) : false;
      const material = (object as THREE.Mesh).material as THREE.Material;
      clip(material, s.planes, sectionOn);
      if (object instanceof THREE.LineSegments) {
        (material as THREE.LineBasicMaterial).color.set(object.userData.index === selected ? Selected : roleColor(element.role).color);
      } else {
        // Выбранный подсвечивается и сам: его контур может целиком уйти за секущий куб.
        (material as THREE.MeshLambertMaterial).emissive.set(object.userData.index === selected ? '#8a6500' : '#000000');
      }
    }

    dispose(s.outline);
    s.outline.clear();
    const chosen = s.group.children.find((c) => c.userData.index === selected);
    if (chosen instanceof THREE.Mesh && chosen.visible) {
      const edges = new THREE.LineSegments(
        new THREE.EdgesGeometry(chosen.geometry, 30),
        new THREE.LineBasicMaterial({ color: Selected, clippingPlanes: sectionOn ? s.planes : [], depthTest: false }),
      );
      s.outline.add(edges);
    }

    s.render();
  }, [scene, meshes, isolation, sectionOn, selected]);

  if (failed)
    return (
      <Center h="100%">
        <Text c="dimmed">Браузер не поддерживает 3D (WebGL)</Text>
      </Center>
    );
  return <div ref={container} style={{ width: '100%', height: '100%' }} />;
}
