import bpy, json, pathlib, sys, numpy as np, gzip, struct
from collections import defaultdict
from mathutils.kdtree import KDTree
root=pathlib.Path(sys.argv[sys.argv.index('--')+1]).resolve()
data=json.loads((root/'prepared-shirt.json').read_text())
bpy.ops.object.select_all(action='SELECT');bpy.ops.object.delete(use_global=False)
mesh=bpy.data.meshes.new('MakeHuman CC0 shirt')
mesh.from_pydata([(x,-z,y) for x,y,z in data['positions']],[],data['faces']);mesh.update()
obj=bpy.data.objects.new('Real shirt',mesh);bpy.context.collection.objects.link(obj)
bpy.context.view_layer.objects.active=obj;obj.select_set(True)
for i in range(8):mesh.materials.append(bpy.data.materials.new('part-'+str(i)))
original=mesh.uv_layers.new(name='Original UV');canonical=mesh.uv_layers.new(name='Print UV')
for p,part,orig,uv in zip(mesh.polygons,data['parts'],data['uvOriginal'],data['uvCanonical']):
 p.material_index=part;p.use_smooth=True
 for index,a,b in zip(p.loop_indices,orig,uv):original.data[index].uv=a;canonical.data[index].uv=b
mesh.uv_layers.active=canonical
# Identify the real neck opening; retain a narrow unprinted collar band.
edgefaces=defaultdict(int);adj=defaultdict(list)
for p in mesh.polygons:
 for a,b in p.edge_keys:edgefaces[tuple(sorted((a,b)))]+=1
for (a,b),count in edgefaces.items():
 if count==1:adj[a].append(b);adj[b].append(a)
remaining=set(adj);loops=[]
while remaining:
 start=next(iter(remaining));todo=[start];group=set()
 while todo:
  a=todo.pop()
  if a in group:continue
  group.add(a);todo.extend(adj[a])
 remaining.difference_update(group);loops.append(group)
neck=max(loops,key=lambda ids:np.mean([mesh.vertices[i].co.z for i in ids]))
tree=KDTree(len(neck))
for n,i in enumerate(neck):tree.insert(mesh.vertices[i].co,n)
tree.balance();collars=0
for p in mesh.polygons:
 if np.mean([tree.find(mesh.vertices[i].co)[2] for i in p.vertices])<.012:
  p.material_index=6;collars+=1
print('Raw shirt',len(mesh.vertices),len(mesh.polygons),'openings',len(loops),'collar faces',collars,flush=True)
sub=obj.modifiers.new('Smooth authored surface','SUBSURF');sub.levels=2
bpy.ops.object.modifier_apply(modifier=sub.name)
solid=obj.modifiers.new('Fabric thickness','SOLIDIFY');solid.thickness=.001;solid.offset=-1
solid.material_offset=7;solid.material_offset_rim=7;solid.use_even_offset=True
bpy.ops.object.modifier_apply(modifier=solid.name)
mesh=obj.data
for p in mesh.polygons:p.use_smooth=True
mesh.update();mesh.calc_loop_triangles()
positions=np.empty((len(mesh.vertices),3),np.float32);mesh.vertices.foreach_get('co',positions.ravel())
normals=np.empty_like(positions);mesh.vertices.foreach_get('normal',normals.ravel())
edges=np.empty((len(mesh.edges),2),np.int32);mesh.edges.foreach_get('vertices',edges.ravel())
degree=np.bincount(edges.ravel(),minlength=len(positions))[:,None]
smooth=positions.astype(np.float64)
# A mild Taubin relaxation forms the low-relief endpoint of the fold slider.
for _ in range(9):
 for factor in [.48,-.51]:
  total=np.zeros_like(smooth);np.add.at(total,edges[:,0],smooth[edges[:,1]]);np.add.at(total,edges[:,1],smooth[edges[:,0]])
  smooth+=factor*(total/np.maximum(1,degree)-smooth)
delta=smooth-positions;length=np.linalg.norm(delta,axis=1)
smooth=positions+delta*np.minimum(1,.009/np.maximum(length,1e-12))[:,None]
sm=mesh.copy();sm.vertices.foreach_set('co',smooth.astype(np.float32).ravel());sm.update()
smoothnorm=np.empty_like(normals);sm.vertices.foreach_get('normal',smoothnorm.ravel())
# Bake the authored tangent normal map into vertex normals. WPF can then show
# fine cloth folds without a separate shader engine or runtime Blender.
normalImage=bpy.data.images.load(data['normal']);normalImage.colorspace_settings.name='Non-Color'
normalPixels=np.empty(normalImage.size[0]*normalImage.size[1]*4,np.float32);normalImage.pixels.foreach_get(normalPixels)
normalPixels=normalPixels.reshape(normalImage.size[1],normalImage.size[0],4)
outer=[t for t in mesh.loop_triangles if t.material_index<7]
vi=np.array([t.vertices[:] for t in outer]);li=np.array([t.loops[:] for t in outer])
originalUvs=mesh.uv_layers['Original UV'].data
tex=np.array([[originalUvs[j].uv[:] for j in loops] for loops in li])
p=positions[vi];edge1=p[:,1]-p[:,0];edge2=p[:,2]-p[:,0];du=tex[:,1]-tex[:,0];dv=tex[:,2]-tex[:,0]
det=du[:,0]*dv[:,1]-du[:,1]*dv[:,0];safe=np.where(abs(det)>1e-12,det,1)
tan=(edge1*dv[:,1,None]-edge2*du[:,1,None])/safe[:,None]
bit=(edge2*du[:,0,None]-edge1*dv[:,0,None])/safe[:,None]
base=normals[vi];tangent=tan[:,None,:]-base*np.sum(base*tan[:,None,:],axis=2)[:,:,None]
tangent/=np.maximum(1e-12,np.linalg.norm(tangent,axis=2))[:,:,None]
bitangent=np.cross(base,tangent);sign=np.sign(np.sum(bitangent*bit[:,None,:],axis=2))
bitangent*=sign[:,:,None]
tx=np.clip(np.round(tex[:,:,0]*(normalImage.size[0]-1)).astype(int),0,normalImage.size[0]-1)
ty=np.clip(np.round(tex[:,:,1]*(normalImage.size[1]-1)).astype(int),0,normalImage.size[1]-1)
detail=normalPixels[ty,tx,:3]*2-1;detail[:,:,:2]*=.75
detail[detail[:,:,2]<.35]=[0,0,1]
detail/=np.maximum(1e-12,np.linalg.norm(detail,axis=2))[:,:,None]
authored=tangent*detail[:,:,0,None]+bitangent*detail[:,:,1,None]+base*detail[:,:,2,None]
area=np.linalg.norm(np.cross(edge1,edge2),axis=1)
accum=np.zeros_like(normals);np.add.at(accum,vi.ravel(),(authored*area[:,None,None]).reshape(-1,3))
valid=np.linalg.norm(accum,axis=1)>1e-12
normals[valid]=accum[valid]/np.linalg.norm(accum[valid],axis=1)[:,None]
print('Authored fabric normals baked',int(valid.sum()),flush=True)
# Blender is Z-up; the native WPF viewer is Y-up and faces +Z.
def native(a):return np.stack([a[:,0],a[:,2],-a[:,1]],axis=1)
positions=native(positions);smooth=native(smooth);normals=native(normals);smoothnorm=native(smoothnorm)
parts={i:{'vertices':[],'triangles':[],'lookup':{}} for i in range(8)}
uvs=mesh.uv_layers['Print UV'].data;sourceuv=mesh.uv_layers['Original UV'].data
shade=np.ones((6,512,512),np.float32);covered=np.zeros_like(shade,dtype=bool)
ao=bpy.data.images.load(data['ao']);ao.colorspace_settings.name='Non-Color'
pixels=np.empty(ao.size[0]*ao.size[1]*4,np.float32);ao.pixels.foreach_get(pixels);pixels=pixels.reshape(ao.size[1],ao.size[0],4)
for tri in mesh.loop_triangles:
 part=tri.material_index;output=parts[part];xy=np.array([uvs[l].uv[:] for l in tri.loops])
 for vi,li,uv in zip(tri.vertices,tri.loops,xy):
  key=(vi,round(float(uv[0]),8),round(float(uv[1]),8));index=output['lookup'].get(key)
  if index is None:
   index=len(output['vertices']);output['lookup'][key]=index
   output['vertices'].append([*positions[vi],*smooth[vi],*normals[vi],*smoothnorm[vi],*uv])
  output['triangles'].append(index)
 if part>=6:continue
 # Bake the source ambient-occlusion map into the six print coordinate charts.
 q=xy*512-.5;lo=np.maximum(0,np.floor(q.min(0)).astype(int));hi=np.minimum(511,np.ceil(q.max(0)).astype(int))
 if np.any(hi<lo):continue
 xs,ys=np.meshgrid(np.arange(lo[0],hi[0]+1),np.arange(lo[1],hi[1]+1))
 ax,ay=q[0];bx,by=q[1];cx,cy=q[2];den=(by-cy)*(ax-cx)+(cx-bx)*(ay-cy)
 if abs(den)<1e-10:continue
 a=((by-cy)*(xs-cx)+(cx-bx)*(ys-cy))/den;b=((cy-ay)*(xs-cx)+(ax-cx)*(ys-cy))/den;c=1-a-b
 mask=(a>=-1e-5)&(b>=-1e-5)&(c>=-1e-5)
 if not mask.any():continue
 old=np.array([sourceuv[l].uv[:] for l in tri.loops]);tx=a*old[0,0]+b*old[1,0]+c*old[2,0];ty=a*old[0,1]+b*old[1,1]+c*old[2,1]
 xx=np.clip(np.round(tx*(ao.size[0]-1)).astype(int),0,ao.size[0]-1);yy=np.clip(np.round(ty*(ao.size[1]-1)).astype(int),0,ao.size[1]-1)
 block=shade[part,lo[1]:hi[1]+1,lo[0]:hi[0]+1];block[mask]=pixels[yy,xx,0][mask]
 covered[part,lo[1]:hi[1]+1,lo[0]:hi[0]+1][mask]=True
asset=root/'shirt.mesh.gz'
with asset.open('wb') as raw, gzip.GzipFile(filename='',mode='wb',fileobj=raw,compresslevel=9,mtime=0) as f:
 f.write(b'VTSHIRT1');f.write(struct.pack('<I',8))
 for part,o in parts.items():
  f.write(struct.pack('<III',part,len(o['vertices']),len(o['triangles'])))
  f.write(np.asarray(o['vertices'],dtype='<f4').tobytes());f.write(np.asarray(o['triangles'],dtype='<i4').tobytes())
print('Mesh baked',len(mesh.vertices),len(mesh.loop_triangles),'bytes',asset.stat().st_size,flush=True)
np.savez_compressed(root/'shade-prepared.npz',shade=shade,covered=covered)
bpy.ops.wm.save_as_mainfile(filepath=str(root/'shirt.blend'))
print('Saved model source and shading',flush=True)
