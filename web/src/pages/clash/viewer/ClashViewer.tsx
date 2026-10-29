import { useEffect, useRef, useState } from 'react';
import { Center, Text } from '@mantine/core';
import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import type { ViewerSceneView } from '../../../api/types';
import { type Box, type Isolation, isVisible, roleColor, type ViewerMesh } from '../../../lib/viewer';

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
}

interface Stage {
  renderer: THREE.WebGLRenderer;
  camera: THREE.PerspectiveCamera;
  controls: OrbitControls;
  world: THREE.Scene;
  group: THREE.Group;
  outline: THREE.Group;
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

function sectionPlanes(box: Box | null): THREE.Plane[] {
  if (!box) return [];
  return [0, 1, 2].flatMap((axis) => {
    const normal = new THREE.Vector3();
    normal.setComponent(axis, 1);
    return [new THREE.Plane(normal.clone(), -box.min[axis]), new THREE.Plane(normal.clone().negate(), box.max[axis])];
  });
}

function inside(point: THREE.Vector3, box: Box | null): boolean {
  if (!box) return true;
  return [0, 1, 2].every((axis) => point.getComponent(axis) >= box.min[axis] && point.getComponent(axis) <= box.max[axis]);
}

/**
 * Сцена коллизии на three.js. Начало координат — точка коллизии, единицы — мм, ось Z вверх (как в Revit).
 * A и B — контрастные цвета, окружение полупрозрачное, элементы без сетки — каркасы габаритов.
 */
export default function ClashViewer({ scene, meshes, isolation, sectionBox, selected, onPick }: ClashViewerProps) {
  const container = useRef<HTMLDivElement>(null);
  const stage = useRef<Stage | null>(null);
  const pick = useRef(onPick);
  pick.current = onPick;
  const section = useRef(sectionBox);
  section.current = sectionBox;
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
    world.add(group, outline);

    const controls = new OrbitControls(camera, renderer.domElement);
    const render = () => renderer.render(world, camera);
    controls.addEventListener('change', render);
    stage.current = { renderer, camera, controls, world, group, outline, render };

    const resize = () => {
      const { clientWidth, clientHeight } = host;
      if (clientWidth === 0 || clientHeight === 0) return;
      renderer.setSize(clientWidth, clientHeight);
      camera.aspect = clientWidth / clientHeight;
      camera.updateProjectionMatrix();
      render();
    };
    const observer = new ResizeObserver(resize);
    observer.observe(host);
    resize();

    // Щелчок без перетаскивания — выбор элемента; перетаскивание вращает камеру.
    let down: { x: number; y: number } | null = null;
    const onDown = (e: PointerEvent) => (down = { x: e.clientX, y: e.clientY });
    const onUp = (e: PointerEvent) => {
      if (!down || Math.hypot(e.clientX - down.x, e.clientY - down.y) > 4) return;
      const rect = renderer.domElement.getBoundingClientRect();
      const pointer = new THREE.Vector2(((e.clientX - rect.left) / rect.width) * 2 - 1, -((e.clientY - rect.top) / rect.height) * 2 + 1);
      const raycaster = new THREE.Raycaster();
      raycaster.params.Line = { threshold: camera.position.length() / 200 };
      raycaster.setFromCamera(pointer, camera);
      const hit = raycaster
        .intersectObjects(group.children.filter((c) => c.visible), false)
        .find((i) => inside(i.point, section.current));
      pick.current(hit ? (hit.object.userData.index as number) : null);
    };
    renderer.domElement.addEventListener('pointerdown', onDown);
    renderer.domElement.addEventListener('pointerup', onUp);

    return () => {
      observer.disconnect();
      renderer.domElement.removeEventListener('pointerdown', onDown);
      renderer.domElement.removeEventListener('pointerup', onUp);
      controls.dispose();
      dispose(world);
      renderer.dispose();
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

  // Камера — на коллизию, когда открыта новая коллизия (не при подгрузке сеток).
  useEffect(() => {
    const s = stage.current;
    if (!s) return;
    const pair = scene.elements.filter((e) => e.role !== 'context');
    const extent = Math.max(
      300,
      ...pair.flatMap((e) => [0, 1, 2].map((axis) => Math.max(Math.abs(e.min[axis]), Math.abs(e.max[axis])))),
    );
    const distance = Math.min(extent * 4, scene.radiusMm * 1.5);
    s.camera.position.copy(new THREE.Vector3(1, -1.2, 0.8).normalize().multiplyScalar(distance));
    s.camera.near = Math.max(1, distance / 2000);
    s.camera.far = distance * 20 + scene.radiusMm * 4;
    s.camera.updateProjectionMatrix();
    s.controls.target.set(0, 0, 0);
    s.controls.update();
    s.render();
  }, [scene]);

  // Изоляция, секущий куб и подсветка выбранного — без пересборки сеток.
  useEffect(() => {
    const s = stage.current;
    if (!s) return;
    const planes = sectionPlanes(sectionBox);
    for (const object of s.group.children) {
      const element = scene.elements[object.userData.index as number];
      object.visible = element ? isVisible(element, isolation, selected) : false;
      const material = (object as THREE.Mesh).material as THREE.Material;
      material.clippingPlanes = planes;
      material.needsUpdate = true;
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
        new THREE.LineBasicMaterial({ color: Selected, clippingPlanes: planes, depthTest: false }),
      );
      s.outline.add(edges);
    }

    s.render();
  }, [scene, meshes, isolation, sectionBox, selected]);

  if (failed)
    return (
      <Center h="100%">
        <Text c="dimmed">Браузер не поддерживает 3D (WebGL)</Text>
      </Center>
    );
  return <div ref={container} style={{ width: '100%', height: '100%' }} />;
}
